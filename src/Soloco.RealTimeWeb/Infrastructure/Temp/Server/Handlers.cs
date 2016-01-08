/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/aspnet-contrib/AspNet.Security.OpenIdConnect.Server
 * for more information concerning the license and the contributors participating to this project.
 */

using System;
using System.Globalization;
using System.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using AspNet.Security.OpenIdConnect.Extensions;
using Microsoft.AspNet.Authentication;
using Microsoft.AspNet.Builder;
using Microsoft.AspNet.Http.Authentication;
using Microsoft.AspNet.Http.Features.Authentication;
using Microsoft.AspNet.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.Net.Http.Headers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Linq;
using System.Security.Cryptography;
using System.Xml;
using System.Collections.Generic;
using Microsoft.Extensions.Caching.Distributed;

namespace AspNet.Security.OpenIdConnect.Server {
    internal partial class OpenIdConnectServerHandler : AuthenticationHandler<OpenIdConnectServerOptions> {
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync() {
            var notification = new MatchEndpointContext(Context, Options);

            if (Options.AuthorizationEndpointPath.HasValue &&
                Options.AuthorizationEndpointPath == Request.Path) {
                notification.MatchesAuthorizationEndpoint();
            }

            else if (Options.LogoutEndpointPath.HasValue &&
                     Options.LogoutEndpointPath == Request.Path) {
                notification.MatchesLogoutEndpoint();
            }

            else if (Options.ProfileEndpointPath.HasValue &&
                     Options.ProfileEndpointPath == Request.Path) {
                notification.MatchesProfileEndpoint();
            }

            await Options.Provider.MatchEndpoint(notification);
            
            if (!notification.IsAuthorizationEndpoint &&
                !notification.IsLogoutEndpoint &&
                !notification.IsProfileEndpoint) {
                return null;
            }

            // Try to retrieve the current OpenID Connect request from the ASP.NET context.
            // If the request cannot be found, this means that this middleware was configured
            // to use the automatic authentication mode and that HandleAuthenticateAsync
            // was invoked before Invoke*EndpointAsync: in this case, the OpenID Connect
            // request is directly extracted from the query string or the request form.
            var request = Context.GetOpenIdConnectRequest();
            if (request == null) {
                if (string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase)) {
                    request = new OpenIdConnectMessage(Request.Query.ToDictionary());
                }

                else if (string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase)) {
                    if (string.IsNullOrEmpty(Request.ContentType)) {
                        return null;
                    }

                    else if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) {
                        return null;
                    }

                    var form = await Request.ReadFormAsync(Context.RequestAborted);

                    request = new OpenIdConnectMessage(form.ToDictionary());
                }
            }

            // Missing or invalid requests are ignored in HandleAuthenticateAsync:
            // in this case, null is always returned to indicate authentication failed.
            if (request == null) {
                return null;
            }

            if (notification.IsAuthorizationEndpoint || notification.IsLogoutEndpoint) {
                if (string.IsNullOrEmpty(request.IdTokenHint)) {
                    return null;
                }

                var ticket = await DeserializeIdentityTokenAsync(request.IdTokenHint, request);
                if (ticket == null) {
                    Logger.LogVerbose("Invalid id_token_hint");

                    return null;
                }

                // Tickets are returned even if they
                // are considered invalid (e.g expired).
                return AuthenticateResult.Success(ticket);
            }

            else if (notification.IsProfileEndpoint) {
                string token;
                if (!string.IsNullOrEmpty(request.AccessToken)) {
                    token = request.AccessToken;
                }

                else {
                    string header = Request.Headers[HeaderNames.Authorization];
                    if (string.IsNullOrEmpty(header)) {
                        return null;
                    }

                    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) {
                        return null;
                    }

                    token = header.Substring("Bearer ".Length);
                    if (string.IsNullOrWhiteSpace(token)) {
                        return null;
                    }
                }

                var ticket = await DeserializeAccessTokenAsync(token, request);
                if (ticket == null) {
                    Logger.LogVerbose("Invalid access_token");

                    return null;
                }

                if (!ticket.Properties.ExpiresUtc.HasValue ||
                     ticket.Properties.ExpiresUtc < Options.SystemClock.UtcNow) {
                    Logger.LogVerbose("Expired access_token");

                    return null;
                }

                return AuthenticateResult.Success(ticket);
            }

            return null;
        }

        public override async Task<bool> HandleRequestAsync() {
            var notification = new MatchEndpointContext(Context, Options);

            if (Options.AuthorizationEndpointPath.HasValue &&
                Options.AuthorizationEndpointPath == Request.Path) {
                notification.MatchesAuthorizationEndpoint();
            }

            else if (Options.TokenEndpointPath.HasValue &&
                     Options.TokenEndpointPath == Request.Path) {
                notification.MatchesTokenEndpoint();
            }

            else if (Options.ValidationEndpointPath.HasValue &&
                     Options.ValidationEndpointPath == Request.Path) {
                notification.MatchesValidationEndpoint();
            }

            else if (Options.ProfileEndpointPath.HasValue &&
                     Options.ProfileEndpointPath == Request.Path) {
                notification.MatchesProfileEndpoint();
            }

            else if (Options.LogoutEndpointPath.HasValue &&
                     Options.LogoutEndpointPath == Request.Path) {
                notification.MatchesLogoutEndpoint();
            }

            else if (Options.ConfigurationEndpointPath.HasValue &&
                     Options.ConfigurationEndpointPath == Request.Path) {
                notification.MatchesConfigurationEndpoint();
            }

            else if (Options.CryptographyEndpointPath.HasValue &&
                     Options.CryptographyEndpointPath == Request.Path) {
                notification.MatchesCryptographyEndpoint();
            }

            await Options.Provider.MatchEndpoint(notification);

            if (notification.HandledResponse) {
                return true;
            }

            else if (notification.Skipped) {
                return false;
            }

            // Reject non-HTTPS requests handled by the OpenID Connect
            // server middleware if AllowInsecureHttp is not set to true.
            if (!Options.AllowInsecureHttp && !Request.IsHttps) {
                Logger.LogWarning("The HTTP request was rejected because AllowInsecureHttp was false.");

                if (notification.IsAuthorizationEndpoint || notification.IsLogoutEndpoint) {
                    // Return the native error page for endpoints involving the user participation.
                    await SendNativeErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "This server only accepts HTTPS requests."
                    });

                    return true;
                }

                else if (notification.IsTokenEndpoint || notification.IsProfileEndpoint ||
                         notification.IsValidationEndpoint || notification.IsConfigurationEndpoint ||
                         notification.IsCryptographyEndpoint) {
                    // Return a JSON error for endpoints that don't involve the user participation.
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "This server only accepts HTTPS requests."
                    });

                    return true;
                }
            }

            if (notification.IsAuthorizationEndpoint) {
                return await InvokeAuthorizationEndpointAsync();
            }

            else if (notification.IsLogoutEndpoint) {
                return await InvokeLogoutEndpointAsync();
            }

            else if (notification.IsTokenEndpoint) {
                await InvokeTokenEndpointAsync();
                return true;
            }

            else if (notification.IsValidationEndpoint) {
                await InvokeValidationEndpointAsync();
                return true;
            }

            else if (notification.IsProfileEndpoint) {
                return await InvokeProfileEndpointAsync();
            }

            else if (notification.IsConfigurationEndpoint) {
                await InvokeConfigurationEndpointAsync();
                return true;
            }

            else if (notification.IsCryptographyEndpoint) {
                await InvokeCryptographyEndpointAsync();
                return true;
            }

            return false;
        }

        protected override async Task HandleSignInAsync(SignInContext context) {
            // request may be null when no authorization request has been received
            // or has been already handled by InvokeAuthorizationEndpointAsync.
            var request = Context.GetOpenIdConnectRequest();
            if (request == null) {
                return;
            }

            // Stop processing the request if there's no response grant that matches
            // the authentication type associated with this middleware instance
            // or if the response status code doesn't indicate a successful response.
            if (context == null || Response.StatusCode != 200) {
                return;
            }

            if (Response.HasStarted) {
                Logger.LogCritical(
                    "OpenIdConnectServerHandler.TeardownCoreAsync cannot be called when " +
                    "the response headers have already been sent back to the user agent. " +
                    "Make sure the response body has not been altered and that no middleware " +
                    "has attempted to write to the response stream during this request.");
                return;
            }

            if (!context.Principal.HasClaim(claim => claim.Type == ClaimTypes.NameIdentifier) &&
                !context.Principal.HasClaim(claim => claim.Type == JwtRegisteredClaimNames.Sub)) {
                Logger.LogError("The returned identity doesn't contain the mandatory ClaimTypes.NameIdentifier claim.");

                await SendNativeErrorPageAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.ServerError,
                    ErrorDescription = "no ClaimTypes.NameIdentifier or sub claim found"
                });

                return;
            }

            // redirect_uri is added to the response message since it's not a mandatory parameter
            // in OAuth 2.0 and can be set or replaced from the ValidateClientRedirectUri event.
            var response = new OpenIdConnectMessage {
                RedirectUri = request.RedirectUri,
                State = request.State
            };

            // Associate client_id with all subsequent tickets.
            context.Properties[OpenIdConnectConstants.Extra.ClientId] = request.ClientId;

            if (!string.IsNullOrEmpty(request.Nonce)) {
                // Keep the original nonce parameter for later comparison.
                context.Properties[OpenIdConnectConstants.Extra.Nonce] = request.Nonce;
            }

            if (!string.IsNullOrEmpty(request.RedirectUri)) {
                // Keep the original redirect_uri parameter for later comparison.
                context.Properties[OpenIdConnectConstants.Extra.RedirectUri] = request.RedirectUri;
            }

            // Note: the application is allowed to specify a different "resource"
            // parameter when calling AuthenticationManager.SignInAsync: in this case,
            // don't replace the "resource" property stored in the authentication ticket.
            if (!string.IsNullOrEmpty(request.Resource) &&
                !context.Properties.ContainsKey(OpenIdConnectConstants.Extra.Resource)) {
                // Keep the original resource parameter for later comparison.
                context.Properties[OpenIdConnectConstants.Extra.Resource] = request.Resource;
            }

            // Note: the application is allowed to specify a different "scope"
            // parameter when calling AuthenticationManager.SignInAsync: in this case,
            // don't replace the "scope" property stored in the authentication ticket.
            if (!string.IsNullOrEmpty(request.Scope) &&
                !context.Properties.ContainsKey(OpenIdConnectConstants.Extra.Scope)) {
                // Keep the original scope parameter for later comparison.
                context.Properties[OpenIdConnectConstants.Extra.Scope] = request.Scope;
            }

            // Determine whether an authorization code should be returned
            // and invoke SerializeAuthorizationCodeAsync if necessary.
            if (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.Code)) {
                // Make sure to create a copy of the authentication properties
                // to avoid modifying the properties set on the original ticket.
                var properties = new AuthenticationProperties(context.Properties).Copy();

                // properties.IssuedUtc and properties.ExpiresUtc are always
                // explicitly set to null to avoid aligning the expiration date
                // of the authorization code with the lifetime of the other tokens.
                properties.IssuedUtc = properties.ExpiresUtc = null;

                response.Code = await SerializeAuthorizationCodeAsync(context.Principal, properties, request, response);

                // Ensure that an authorization code is issued to avoid returning an invalid response.
                // See http://openid.net/specs/oauth-v2-multiple-response-types-1_0.html#Combinations
                if (string.IsNullOrEmpty(response.Code)) {
                    Logger.LogError("SerializeAuthorizationCodeAsync returned no authorization code");

                    await SendNativeErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.ServerError,
                        ErrorDescription = "no valid authorization code was issued"
                    });

                    return;
                }
            }

            // Determine whether an access token should be returned
            // and invoke SerializeAccessTokenAsync if necessary.
            if (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.Token)) {
                // Make sure to create a copy of the authentication properties
                // to avoid modifying the properties set on the original ticket.
                var properties = new AuthenticationProperties(context.Properties).Copy();

                // Note: when the "resource" parameter added to the OpenID Connect response
                // is identical to the request parameter, keeping it is not necessary.
                var resource = properties.GetProperty(OpenIdConnectConstants.Extra.Resource);
                if (!string.Equals(request.Resource, resource, StringComparison.Ordinal)) {
                    response.Resource = resource;
                }

                // Note: when the "scope" parameter added to the OpenID Connect response
                // is identical to the request parameter, keeping it is not necessary.
                var scope = properties.GetProperty(OpenIdConnectConstants.Extra.Scope);
                if (!string.Equals(request.Scope, scope, StringComparison.Ordinal)) {
                    response.Scope = scope;
                }

                response.TokenType = OpenIdConnectConstants.TokenTypes.Bearer;
                response.AccessToken = await SerializeAccessTokenAsync(context.Principal, properties, request, response);

                // Ensure that an access token is issued to avoid returning an invalid response.
                // See http://openid.net/specs/oauth-v2-multiple-response-types-1_0.html#Combinations
                if (string.IsNullOrEmpty(response.AccessToken)) {
                    Logger.LogError("SerializeAccessTokenAsync returned no access token.");

                    await SendNativeErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.ServerError,
                        ErrorDescription = "no valid access token was issued"
                    });

                    return;
                }

                // properties.ExpiresUtc is automatically set by SerializeAccessTokenAsync but the end user
                // is free to set a null value directly in the SerializeAccessToken event.
                if (properties.ExpiresUtc.HasValue && properties.ExpiresUtc > Options.SystemClock.UtcNow) {
                    var lifetime = properties.ExpiresUtc.Value - Options.SystemClock.UtcNow;
                    var expiration = (long) (lifetime.TotalSeconds + .5);

                    response.ExpiresIn = expiration.ToString(CultureInfo.InvariantCulture);
                }
            }

            // Determine whether an identity token should be returned
            // and invoke SerializeIdentityTokenAsync if necessary.
            // Note: the identity token MUST be created after the authorization code
            // and the access token to create appropriate at_hash and c_hash claims.
            if (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.IdToken)) {
                // Make sure to create a copy of the authentication properties
                // to avoid modifying the properties set on the original ticket.
                var properties = new AuthenticationProperties(context.Properties).Copy();

                response.IdToken = await SerializeIdentityTokenAsync(context.Principal, properties, request, response);

                // Ensure that an identity token is issued to avoid returning an invalid response.
                // See http://openid.net/specs/oauth-v2-multiple-response-types-1_0.html#Combinations
                if (string.IsNullOrEmpty(response.IdToken)) {
                    Logger.LogError("SerializeIdentityTokenAsync returned no identity token.");

                    await SendNativeErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.ServerError,
                        ErrorDescription = "no valid identity token was issued",
                    });

                    return;
                }
            }

            // Remove the OpenID Connect request from the distributed cache.
            var identifier = request.GetUniqueIdentifier();
            if (!string.IsNullOrEmpty(identifier)) {
                await Options.Cache.RemoveAsync(identifier);
            }

            var ticket = new AuthenticationTicket(context.Principal,
                new AuthenticationProperties(context.Properties),
                context.AuthenticationScheme);

            var notification = new AuthorizationEndpointResponseContext(Context, Options, ticket, request, response);
            await Options.Provider.AuthorizationEndpointResponse(notification);

            if (notification.HandledResponse) {
                return;
            }

            await ApplyAuthorizationResponseAsync(request, response);
        }

        protected override async Task HandleSignOutAsync(SignOutContext context) {
            // request may be null when no logout request has been received
            // or has been already handled by InvokeLogoutEndpointAsync.
            var request = Context.GetOpenIdConnectRequest();
            if (request == null) {
                return;
            }

            // Stop processing the request if there's no signout context that matches
            // the authentication type associated with this middleware instance
            // or if the response status code doesn't indicate a successful response.
            if (context == null || Response.StatusCode != 200) {
                return;
            }

            if (Response.HasStarted) {
                Logger.LogCritical(
                    "OpenIdConnectServerHandler.TeardownCoreAsync cannot be called when " +
                    "the response headers have already been sent back to the user agent. " +
                    "Make sure the response body has not been altered and that no middleware " +
                    "has attempted to write to the response stream during this request.");
                return;
            }

            // post_logout_redirect_uri is added to the response message since it can be
            // set or replaced from the ValidateClientLogoutRedirectUri event.
            var response = new OpenIdConnectMessage {
                PostLogoutRedirectUri = request.PostLogoutRedirectUri,
                State = request.State
            };

            var notification = new LogoutEndpointResponseContext(Context, Options, request, response);
            await Options.Provider.LogoutEndpointResponse(notification);

            if (notification.HandledResponse) {
                return;
            }

            // Stop processing the request if no explicit
            // post_logout_redirect_uri has been provided.
            if (string.IsNullOrEmpty(response.PostLogoutRedirectUri)) {
                return;
            }

            var location = response.PostLogoutRedirectUri;

            foreach (var parameter in response.Parameters) {
                // Don't include post_logout_redirect_uri in the query string.
                if (string.Equals(parameter.Key, OpenIdConnectParameterNames.PostLogoutRedirectUri, StringComparison.Ordinal)) {
                    continue;
                }

                location = QueryHelpers.AddQueryString(location, parameter.Key, parameter.Value);
            }

            Response.Redirect(location);
        }

        protected override async Task<bool> HandleUnauthorizedAsync(ChallengeContext context) {
            var notification = new MatchEndpointContext(Context, Options);

            if (Options.ProfileEndpointPath.HasValue &&
                Options.ProfileEndpointPath == Request.Path) {
                notification.MatchesProfileEndpoint();
            }

            await Options.Provider.MatchEndpoint(notification);

            // Return true to indicate to the authentication pipeline that
            // the 401 response shouldn't be handled by the other middleware.
            if (!notification.IsProfileEndpoint) {
                return true;
            }

            Response.StatusCode = 401;
            Response.Headers[HeaderNames.WWWAuthenticate] = "error=" + OpenIdConnectConstants.Errors.InvalidGrant;

            return false;
        }

        protected override async Task FinishResponseAsync() {
            // Stop processing the request if no OpenID Connect
            // message has been found in the current context.
            var request = Context.GetOpenIdConnectRequest();
            if (request == null) {
                return;
            }

            // Don't apply any modification to the response if no OpenID Connect
            // message has been explicitly created by the inner application.
            var response = Context.GetOpenIdConnectResponse();
            if (response == null) {
                return;
            }

            // Successful authorization responses are directly applied by
            // HandleSignInAsync: only error responses should be handled at this stage.
            if (string.IsNullOrEmpty(response.Error)) {
                return;
            }

            await SendErrorRedirectAsync(request, response);
        }

        private async Task<bool> ApplyAuthorizationResponseAsync(OpenIdConnectMessage request, OpenIdConnectMessage response) {
            if (request.IsFormPostResponseMode()) {
                using (var buffer = new MemoryStream())
                using (var writer = new StreamWriter(buffer)) {
                    writer.WriteLine("<!doctype html>");
                    writer.WriteLine("<html>");
                    writer.WriteLine("<body>");

                    // While the redirect_uri parameter should be guarded against unknown values
                    // by IOpenIdConnectServerProvider.ValidateClientRedirectUri,
                    // it's still safer to encode it to avoid cross-site scripting attacks
                    // if the authorization server has a relaxed policy concerning redirect URIs.
                    writer.WriteLine("<form name='form' method='post' action='" + Options.HtmlEncoder.HtmlEncode(response.RedirectUri) + "'>");

                    foreach (var parameter in response.Parameters) {
                        // Don't include redirect_uri in the form.
                        if (string.Equals(parameter.Key, OpenIdConnectParameterNames.RedirectUri, StringComparison.Ordinal)) {
                            continue;
                        }

                        var name = Options.HtmlEncoder.HtmlEncode(parameter.Key);
                        var value = Options.HtmlEncoder.HtmlEncode(parameter.Value);

                        writer.WriteLine("<input type='hidden' name='" + name + "' value='" + value + "' />");
                    }

                    writer.WriteLine("<noscript>Click here to finish the authorization process: <input type='submit' /></noscript>");
                    writer.WriteLine("</form>");
                    writer.WriteLine("<script>document.form.submit();</script>");
                    writer.WriteLine("</body>");
                    writer.WriteLine("</html>");
                    writer.Flush();

                    Response.ContentLength = buffer.Length;
                    Response.ContentType = "text/html;charset=UTF-8";

                    buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                    await buffer.CopyToAsync(Response.Body, 4096, Context.RequestAborted);

                    return true;
                }
            }

            else if (request.IsFragmentResponseMode()) {
                var location = response.RedirectUri;
                var appender = new Appender(location, '#');

                foreach (var parameter in response.Parameters) {
                    // Don't include redirect_uri in the fragment.
                    if (string.Equals(parameter.Key, OpenIdConnectParameterNames.RedirectUri, StringComparison.Ordinal)) {
                        continue;
                    }

                    appender.Append(parameter.Key, parameter.Value);
                }

                Response.Redirect(appender.ToString());
                return true;
            }

            else if (request.IsQueryResponseMode()) {
                var location = response.RedirectUri;

                foreach (var parameter in response.Parameters) {
                    // Don't include redirect_uri in the query string.
                    if (string.Equals(parameter.Key, OpenIdConnectParameterNames.RedirectUri, StringComparison.Ordinal)) {
                        continue;
                    }

                    location = QueryHelpers.AddQueryString(location, parameter.Key, parameter.Value);
                }

                Response.Redirect(location);
                return true;
            }

            return false;
        }

        private async Task<bool> SendErrorRedirectAsync(OpenIdConnectMessage request, OpenIdConnectMessage response) {
            // Remove the authorization request from the ASP.NET context to inform
            // TeardownCoreAsync that there's nothing more to handle.
            Context.SetOpenIdConnectRequest(request: null);

            // Use a generic error if none has been explicitly provided.
            if (string.IsNullOrEmpty(response.Error)) {
                response.Error = OpenIdConnectConstants.Errors.InvalidRequest;
            }

            // Directly display an error page if redirect_uri cannot be used.
            if (string.IsNullOrEmpty(response.RedirectUri)) {
                return await SendErrorPageAsync(response);
            }

            // Try redirecting the user agent to the client
            // application or display a default error page.
            if (!await ApplyAuthorizationResponseAsync(request, response)) {
                return await SendErrorPageAsync(response);
            }

            // Stop processing the request.
            return true;
        }

        private async Task<bool> SendErrorPageAsync(OpenIdConnectMessage response) {
            // Use a generic error if none has been explicitly provided.
            if (string.IsNullOrEmpty(response.Error)) {
                response.Error = OpenIdConnectConstants.Errors.InvalidRequest;
            }

            if (Options.ApplicationCanDisplayErrors) {
                Context.SetOpenIdConnectResponse(response);

                // Request is not handled - pass through to application for rendering.
                return false;
            }

            await SendNativeErrorPageAsync(response);

            // Request is always handled when rendering the default error page.
            return true;
        }

        private async Task SendNativeErrorPageAsync(OpenIdConnectMessage response) {
            using (var buffer = new MemoryStream())
            using (var writer = new StreamWriter(buffer)) {
                foreach (var parameter in response.Parameters) {
                    writer.WriteLine("{0}: {1}", parameter.Key, parameter.Value);
                }

                writer.Flush();

                Response.StatusCode = 400;
                Response.ContentLength = buffer.Length;
                Response.ContentType = "text/plain;charset=UTF-8";

                Response.Headers[HeaderNames.CacheControl] = "no-cache";
                Response.Headers[HeaderNames.Pragma] = "no-cache";
                Response.Headers[HeaderNames.Expires] = "-1";

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Context.RequestAborted);
            }
        }

        private async Task SendPayloadAsync(JToken payload) {
            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer))) {
                payload.WriteTo(writer);
                writer.Flush();

                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Context.RequestAborted);
            }
        }

        private async Task SendErrorPayloadAsync(OpenIdConnectMessage response) {
            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer))) {
                var payload = new JObject();

                payload.Add(OpenIdConnectConstants.Parameters.Error, response.Error);

                if (!string.IsNullOrEmpty(response.ErrorDescription)) {
                    payload.Add(OpenIdConnectConstants.Parameters.ErrorDescription, response.ErrorDescription);
                }

                if (!string.IsNullOrEmpty(response.ErrorUri)) {
                    payload.Add(OpenIdConnectConstants.Parameters.ErrorUri, response.ErrorUri);
                }

                payload.WriteTo(writer);
                writer.Flush();

                Response.StatusCode = 400;
                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                Response.Headers[HeaderNames.CacheControl] = "no-cache";
                Response.Headers[HeaderNames.Pragma] = "no-cache";
                Response.Headers[HeaderNames.Expires] = "-1";

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Context.RequestAborted);
            }
        }

        private string GenerateKey(int length) {
            var bytes = new byte[length];
            Options.RandomNumberGenerator.GetBytes(bytes);
            return Base64UrlEncoder.Encode(bytes);
        }

        private class Appender {
            private readonly char _delimiter;
            private readonly StringBuilder _sb;
            private bool _hasDelimiter;

            public Appender(string value, char delimiter) {
                _sb = new StringBuilder(value);
                _delimiter = delimiter;
                _hasDelimiter = value.IndexOf(delimiter) != -1;
            }

            public Appender Append(string name, string value) {
                _sb.Append(_hasDelimiter ? '&' : _delimiter)
                   .Append(Uri.EscapeDataString(name))
                   .Append('=')
                   .Append(Uri.EscapeDataString(value));
                _hasDelimiter = true;
                return this;
            }

            public override string ToString() {
                return _sb.ToString();
            }
        }

        private async Task<bool> InvokeAuthorizationEndpointAsync()
        {
            OpenIdConnectMessage request;

            if (string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                // Create a new authorization request using the
                // parameters retrieved from the query string.
                request = new OpenIdConnectMessage(Request.Query.ToDictionary())
                {
                    RequestType = OpenIdConnectRequestType.AuthenticationRequest
                };
            }

            else if (string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                // See http://openid.net/specs/openid-connect-core-1_0.html#FormSerialization
                if (string.IsNullOrEmpty(Request.ContentType))
                {
                    Logger.LogInformation("A malformed request has been received by the authorization endpoint.");

                    return await SendErrorPageAsync(new OpenIdConnectMessage
                    {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed authorization request has been received: " +
                            "the mandatory 'Content-Type' header was missing from the POST request."
                    });
                }

                // May have media/type; charset=utf-8, allow partial match.
                if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogInformation("A malformed request has been received by the authorization endpoint.");

                    return await SendErrorPageAsync(new OpenIdConnectMessage
                    {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed authorization request has been received: " +
                            "the 'Content-Type' header contained an unexcepted value. " +
                            "Make sure to use 'application/x-www-form-urlencoded'."
                    });
                }

                // Create a new authorization request using the
                // parameters retrieved from the request form.
                var form = await Request.ReadFormAsync(Context.RequestAborted);

                request = new OpenIdConnectMessage(form.ToDictionary())
                {
                    RequestType = OpenIdConnectRequestType.AuthenticationRequest
                };
            }

            else {
                Logger.LogInformation("A malformed request has been received by the authorization endpoint.");

                return await SendErrorPageAsync(new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed authorization request has been received: " +
                        "make sure to use either GET or POST."
                });
            }

            // Re-assemble the authorization request using the distributed cache if
            // a 'unique_id' parameter has been extracted from the received message.
            var identifier = request.GetUniqueIdentifier();
            if (!string.IsNullOrEmpty(identifier))
            {
                var buffer = await Options.Cache.GetAsync(identifier);
                if (buffer == null)
                {
                    Logger.LogInformation("A unique_id has been provided but no corresponding " +
                                          "OpenID Connect request has been found in the distributed cache.");

                    return await SendErrorPageAsync(new OpenIdConnectMessage
                    {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "Invalid request: timeout expired."
                    });
                }

                using (var stream = new MemoryStream(buffer))
                using (var reader = new BinaryReader(stream))
                {
                    // Make sure the stored authorization request
                    // has been serialized using the same method.
                    var version = reader.ReadInt32();
                    if (version != 1)
                    {
                        await Options.Cache.RemoveAsync(identifier);

                        Logger.LogError("An invalid OpenID Connect request has been found in the distributed cache.");

                        return await SendErrorPageAsync(new OpenIdConnectMessage
                        {
                            Error = OpenIdConnectConstants.Errors.InvalidRequest,
                            ErrorDescription = "Invalid request: timeout expired."
                        });
                    }

                    for (int index = 0, length = reader.ReadInt32(); index < length; index++)
                    {
                        var name = reader.ReadString();
                        var value = reader.ReadString();

                        // Skip restoring the parameter retrieved from the stored request
                        // if the OpenID Connect message extracted from the query string
                        // or the request form defined the same parameter.
                        if (!request.Parameters.ContainsKey(name))
                        {
                            request.SetParameter(name, value);
                        }
                    }
                }
            }

            // Store the authorization request in the ASP.NET context.
            Context.SetOpenIdConnectRequest(request);

            // client_id is mandatory parameter and MUST cause an error when missing.
            // See http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest
            if (string.IsNullOrEmpty(request.ClientId))
            {
                return await SendErrorPageAsync(new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "client_id was missing"
                });
            }

            // While redirect_uri was not mandatory in OAuth2, this parameter
            // is now declared as REQUIRED and MUST cause an error when missing.
            // See http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest
            // To keep AspNet.Security.OpenIdConnect.Server compatible with pure OAuth2 clients,
            // an error is only returned if the request was made by an OpenID Connect client.
            if (string.IsNullOrEmpty(request.RedirectUri) && request.ContainsScope(OpenIdConnectConstants.Scopes.OpenId))
            {
                return await SendErrorPageAsync(new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "redirect_uri must be included when making an OpenID Connect request"
                });
            }

            if (!string.IsNullOrEmpty(request.RedirectUri))
            {
                Uri uri;
                if (!Uri.TryCreate(request.RedirectUri, UriKind.Absolute, out uri))
                {
                    // redirect_uri MUST be an absolute URI.
                    // See http://tools.ietf.org/html/rfc6749#section-3.1.2
                    // and http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest
                    return await SendErrorPageAsync(new OpenIdConnectMessage
                    {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "redirect_uri must be absolute"
                    });
                }

                else if (!string.IsNullOrEmpty(uri.Fragment))
                {
                    // redirect_uri MUST NOT include a fragment component.
                    // See http://tools.ietf.org/html/rfc6749#section-3.1.2
                    // and http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest
                    return await SendErrorPageAsync(new OpenIdConnectMessage
                    {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "redirect_uri must not include a fragment"
                    });
                }

                else if (!Options.AllowInsecureHttp && string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
                {
                    // redirect_uri SHOULD require the use of TLS
                    // http://tools.ietf.org/html/rfc6749#section-3.1.2.1
                    // and http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest
                    return await SendErrorPageAsync(new OpenIdConnectMessage
                    {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "redirect_uri does not meet the security requirements"
                    });
                }
            }

            var clientNotification = new ValidateClientRedirectUriContext(Context, Options, request);
            await Options.Provider.ValidateClientRedirectUri(clientNotification);

            // Reject the authorization request if the redirect_uri was not validated.
            if (!clientNotification.IsValidated)
            {
                Logger.LogVerbose("Unable to validate client information");

                return await SendErrorPageAsync(new OpenIdConnectMessage
                {
                    Error = clientNotification.Error ?? OpenIdConnectConstants.Errors.InvalidClient,
                    ErrorDescription = clientNotification.ErrorDescription,
                    ErrorUri = clientNotification.ErrorUri
                });
            }

            if (!string.IsNullOrEmpty(request.GetParameter(OpenIdConnectConstants.Parameters.Request)))
            {
                Logger.LogVerbose("The authorization request contained the unsupported request parameter.");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.RequestNotSupported,
                    ErrorDescription = "The request parameter is not supported.",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            else if (!string.IsNullOrEmpty(request.RequestUri))
            {
                Logger.LogVerbose("The authorization request contained the unsupported request_uri parameter.");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.RequestUriNotSupported,
                    ErrorDescription = "The request_uri parameter is not supported.",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            else if (string.IsNullOrEmpty(request.ResponseType))
            {
                Logger.LogVerbose("Authorization request missing required response_type parameter");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "response_type parameter missing",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            // Reject requests whose flow is unsupported.
            else if (!request.IsNoneFlow() && !request.IsAuthorizationCodeFlow() &&
                     !request.IsImplicitFlow() && !request.IsHybridFlow())
            {
                Logger.LogVerbose("Authorization request contains unsupported response_type parameter");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.UnsupportedResponseType,
                    ErrorDescription = "response_type unsupported",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            // Reject requests whose response_mode is unsupported.
            else if (!request.IsFormPostResponseMode() && !request.IsFragmentResponseMode() && !request.IsQueryResponseMode())
            {
                Logger.LogVerbose("Authorization request contains unsupported response_mode parameter");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "response_mode unsupported",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            // response_mode=query (explicit or not) and a response_type containing id_token
            // or token are not considered as a safe combination and MUST be rejected.
            // See http://openid.net/specs/oauth-v2-multiple-response-types-1_0.html#Security
            else if (request.IsQueryResponseMode() && (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.IdToken) ||
                                                       request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.Token)))
            {
                Logger.LogVerbose("Authorization request contains unsafe response_type/response_mode combination");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "response_type/response_mode combination unsupported",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            // Reject OpenID Connect implicit/hybrid requests missing the mandatory nonce parameter.
            // See http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest,
            // http://openid.net/specs/openid-connect-implicit-1_0.html#RequestParameters
            // and http://openid.net/specs/openid-connect-core-1_0.html#HybridIDToken.
            else if (string.IsNullOrEmpty(request.Nonce) && request.ContainsScope(OpenIdConnectConstants.Scopes.OpenId) &&
                                                           (request.IsImplicitFlow() || request.IsHybridFlow()))
            {
                Logger.LogVerbose("The 'nonce' parameter was missing");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "nonce parameter missing",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            // Reject requests containing the id_token response_mode if no openid scope has been received.
            else if (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.IdToken) &&
                    !request.ContainsScope(OpenIdConnectConstants.Scopes.OpenId))
            {
                Logger.LogVerbose("The 'openid' scope part was missing");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "openid scope missing",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            // Reject requests containing the code response_mode if the token endpoint has been disabled.
            else if (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.Code) &&
                    !Options.TokenEndpointPath.HasValue)
            {
                Logger.LogVerbose("Authorization request contains the disabled code response_type");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.UnsupportedResponseType,
                    ErrorDescription = "response_type=code is not supported by this server",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            var validationNotification = new ValidateAuthorizationRequestContext(Context, Options, request);
            await Options.Provider.ValidateAuthorizationRequest(validationNotification);

            // Stop processing the request if Validated was not called.
            if (!validationNotification.IsValidated)
            {
                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage
                {
                    Error = validationNotification.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = validationNotification.ErrorDescription,
                    ErrorUri = validationNotification.ErrorUri,
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            identifier = request.GetUniqueIdentifier();
            if (string.IsNullOrEmpty(identifier))
            {
                // Generate a new 256-bits identifier and associate it with the authorization request.
                identifier = GenerateKey(length: 256 / 8);
                request.SetUniqueIdentifier(identifier);

                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(/* version: */ 1);
                    writer.Write(request.Parameters.Count);

                    foreach (var parameter in request.Parameters)
                    {
                        writer.Write(parameter.Key);
                        writer.Write(parameter.Value);
                    }

                    // Store the authorization request in the distributed cache.
                    await Options.Cache.SetAsync(request.GetUniqueIdentifier(), options => {
                        options.SetAbsoluteExpiration(TimeSpan.FromHours(1));

                        return stream.ToArray();
                    });
                }
            }

            var notification = new AuthorizationEndpointContext(Context, Options, request);
            await Options.Provider.AuthorizationEndpoint(notification);

            if (notification.HandledResponse)
            {
                return true;
            }

            return false;
        }

        private async Task InvokeConfigurationEndpointAsync()
        {
            var notification = new ConfigurationEndpointContext(Context, Options);
            notification.Issuer = Context.GetIssuer(Options);

            // Metadata requests must be made via GET.
            // See http://openid.net/specs/openid-connect-discovery-1_0.html#ProviderConfigurationRequest
            if (!string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogError("Configuration endpoint: invalid method used.");

                return;
            }

            if (Options.AuthorizationEndpointPath.HasValue)
            {
                notification.AuthorizationEndpoint = notification.Issuer.AddPath(Options.AuthorizationEndpointPath);
            }

            if (Options.CryptographyEndpointPath.HasValue)
            {
                notification.CryptographyEndpoint = notification.Issuer.AddPath(Options.CryptographyEndpointPath);
            }

            if (Options.ProfileEndpointPath.HasValue)
            {
                notification.ProfileEndpoint = notification.Issuer.AddPath(Options.ProfileEndpointPath);
            }

            if (Options.ValidationEndpointPath.HasValue)
            {
                notification.ValidationEndpoint = notification.Issuer.AddPath(Options.ValidationEndpointPath);
            }

            if (Options.TokenEndpointPath.HasValue)
            {
                notification.TokenEndpoint = notification.Issuer.AddPath(Options.TokenEndpointPath);
            }

            if (Options.LogoutEndpointPath.HasValue)
            {
                notification.LogoutEndpoint = notification.Issuer.AddPath(Options.LogoutEndpointPath);
            }

            if (Options.AuthorizationEndpointPath.HasValue)
            {
                // Only expose the implicit grant type if the token
                // endpoint has not been explicitly disabled.
                notification.GrantTypes.Add(OpenIdConnectConstants.GrantTypes.Implicit);

                if (Options.TokenEndpointPath.HasValue)
                {
                    // Only expose the authorization code and refresh token grant types
                    // if both the authorization and the token endpoints are enabled.
                    notification.GrantTypes.Add(OpenIdConnectConstants.GrantTypes.AuthorizationCode);
                }
            }

            if (Options.TokenEndpointPath.HasValue)
            {
                notification.GrantTypes.Add(OpenIdConnectConstants.GrantTypes.RefreshToken);

                // If the authorization endpoint is disabled, assume the authorization server will
                // allow the client credentials and resource owner password credentials grant types.
                if (!Options.AuthorizationEndpointPath.HasValue)
                {
                    notification.GrantTypes.Add(OpenIdConnectConstants.GrantTypes.ClientCredentials);
                    notification.GrantTypes.Add(OpenIdConnectConstants.GrantTypes.Password);
                }
            }

            // Only populate response_modes_supported and response_types_supported
            // if the authorization endpoint is available.
            if (Options.AuthorizationEndpointPath.HasValue)
            {
                notification.ResponseModes.Add(OpenIdConnectConstants.ResponseModes.FormPost);
                notification.ResponseModes.Add(OpenIdConnectConstants.ResponseModes.Fragment);
                notification.ResponseModes.Add(OpenIdConnectConstants.ResponseModes.Query);

                notification.ResponseTypes.Add(OpenIdConnectConstants.ResponseTypes.Token);
                notification.ResponseTypes.Add(OpenIdConnectConstants.ResponseTypes.IdToken);

                notification.ResponseTypes.Add(
                    OpenIdConnectConstants.ResponseTypes.IdToken + ' ' +
                    OpenIdConnectConstants.ResponseTypes.Token);

                // Only expose response types containing code when
                // the token endpoint has not been explicitly disabled.
                if (Options.TokenEndpointPath.HasValue)
                {
                    notification.ResponseTypes.Add(OpenIdConnectConstants.ResponseTypes.Code);

                    notification.ResponseTypes.Add(
                        OpenIdConnectConstants.ResponseTypes.Code + ' ' +
                        OpenIdConnectConstants.ResponseTypes.Token);

                    notification.ResponseTypes.Add(
                        OpenIdConnectConstants.ResponseTypes.Code + ' ' +
                        OpenIdConnectConstants.ResponseTypes.IdToken);

                    notification.ResponseTypes.Add(
                        OpenIdConnectConstants.ResponseTypes.Code + ' ' +
                        OpenIdConnectConstants.ResponseTypes.IdToken + ' ' +
                        OpenIdConnectConstants.ResponseTypes.Token);
                }
            }

            notification.Scopes.Add(OpenIdConnectConstants.Scopes.OpenId);

            notification.SubjectTypes.Add(OpenIdConnectConstants.SubjectTypes.Public);

            notification.SigningAlgorithms.Add(OpenIdConnectConstants.Algorithms.RS256);

            await Options.Provider.ConfigurationEndpoint(notification);

            if (notification.HandledResponse)
            {
                return;
            }

            var payload = new JObject();

            payload.Add(OpenIdConnectConstants.Metadata.Issuer, notification.Issuer);

            if (!string.IsNullOrEmpty(notification.AuthorizationEndpoint))
            {
                payload.Add(OpenIdConnectConstants.Metadata.AuthorizationEndpoint, notification.AuthorizationEndpoint);
            }

            if (!string.IsNullOrEmpty(notification.ProfileEndpoint))
            {
                payload.Add(OpenIdConnectConstants.Metadata.UserinfoEndpoint, notification.ProfileEndpoint);
            }

            if (!string.IsNullOrWhiteSpace(notification.ProfileEndpoint))
            {
                payload.Add(OpenIdConnectConstants.Metadata.IntrospectionEndpoint, notification.ValidationEndpoint);
            }

            if (!string.IsNullOrEmpty(notification.TokenEndpoint))
            {
                payload.Add(OpenIdConnectConstants.Metadata.TokenEndpoint, notification.TokenEndpoint);
            }

            if (!string.IsNullOrEmpty(notification.LogoutEndpoint))
            {
                payload.Add(OpenIdConnectConstants.Metadata.EndSessionEndpoint, notification.LogoutEndpoint);
            }

            if (!string.IsNullOrEmpty(notification.CryptographyEndpoint))
            {
                payload.Add(OpenIdConnectConstants.Metadata.JwksUri, notification.CryptographyEndpoint);
            }

            payload.Add(OpenIdConnectConstants.Metadata.GrantTypesSupported,
                JArray.FromObject(notification.GrantTypes.Distinct()));

            payload.Add(OpenIdConnectConstants.Metadata.ResponseModesSupported,
                JArray.FromObject(notification.ResponseModes.Distinct()));

            payload.Add(OpenIdConnectConstants.Metadata.ResponseTypesSupported,
                JArray.FromObject(notification.ResponseTypes.Distinct()));

            payload.Add(OpenIdConnectConstants.Metadata.SubjectTypesSupported,
                JArray.FromObject(notification.SubjectTypes.Distinct()));

            payload.Add(OpenIdConnectConstants.Metadata.ScopesSupported,
                JArray.FromObject(notification.Scopes.Distinct()));

            payload.Add(OpenIdConnectConstants.Metadata.IdTokenSigningAlgValuesSupported,
                JArray.FromObject(notification.SigningAlgorithms.Distinct()));

            var context = new ConfigurationEndpointResponseContext(Context, Options, payload);
            await Options.Provider.ConfigurationEndpointResponse(context);

            if (context.HandledResponse)
            {
                return;
            }

            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer)))
            {
                payload.WriteTo(writer);
                writer.Flush();

                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Context.RequestAborted);
            }
        }

        private async Task InvokeCryptographyEndpointAsync()
        {
            var notification = new CryptographyEndpointContext(Context, Options);

            // Metadata requests must be made via GET.
            // See http://openid.net/specs/openid-connect-discovery-1_0.html#ProviderConfigurationRequest
            if (!string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogError("Cryptography endpoint: invalid method used.");

                return;
            }

            foreach (var credentials in Options.SigningCredentials)
            {
                // Ignore the key if it's not supported.
                if (!credentials.Key.IsSupportedAlgorithm(SecurityAlgorithms.RsaSha256Signature) &&
                    !credentials.Key.IsSupportedAlgorithm(SecurityAlgorithms.RsaSha384Signature) &&
                    !credentials.Key.IsSupportedAlgorithm(SecurityAlgorithms.RsaSha512Signature))
                {
                    Logger.LogVerbose("Cryptography endpoint: unsupported signing key ignored. " +
                                      "Only asymmetric security keys supporting RS256, RS384 " +
                                      "or RS512 can be exposed via the JWKS endpoint.");

                    continue;
                }

                // Determine whether the security key is a RSA key embedded in a X.509 certificate.
                var x509SecurityKey = credentials.Key as X509SecurityKey;
                if (x509SecurityKey != null)
                {
                    // Create a new JSON Web Key exposing the
                    // certificate instead of its public RSA key.
                    notification.Keys.Add(new JsonWebKey
                    {
                        Use = JsonWebKeyUseNames.Sig,
                        Kty = JsonWebAlgorithmsKeyTypes.RSA,

                        // Resolve the JWA identifier from the algorithm specified in the credentials.
                        Alg = OpenIdConnectServerHelpers.GetJwtAlgorithm(credentials.Algorithm),

                        // By default, use the key identifier specified in the signing credentials. If none is specified,
                        // use the hexadecimal representation of the certificate's SHA-1 hash as the unique key identifier.
                        Kid = credentials.Kid ?? x509SecurityKey.KeyId ?? x509SecurityKey.Certificate.Thumbprint,

                        // x5t must be base64url-encoded.
                        // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#section-4.8
                        X5t = Base64UrlEncoder.Encode(x509SecurityKey.Certificate.GetCertHash()),

                        // Unlike E or N, the certificates contained in x5c
                        // must be base64-encoded and not base64url-encoded.
                        // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#section-4.7
                        X5c = { Convert.ToBase64String(x509SecurityKey.Certificate.RawData) }
                    });
                }

                var rsaSecurityKey = credentials.Key as RsaSecurityKey;
                if (rsaSecurityKey != null)
                {
                    // Export the RSA public key.
                    notification.Keys.Add(new JsonWebKey
                    {
                        Use = JsonWebKeyUseNames.Sig,
                        Kty = JsonWebAlgorithmsKeyTypes.RSA,

                        // Resolve the JWA identifier from the algorithm specified in the credentials.
                        Alg = OpenIdConnectServerHelpers.GetJwtAlgorithm(credentials.Algorithm),

                        // By default, use the key identifier specified in the signing credentials.
                        // If none is specified, try to extract it from the security key itself or
                        // create one using the base64url-encoded representation of the modulus.
                        Kid = credentials.Kid ?? rsaSecurityKey.KeyId ??
                            // Note: use the first 40 chars to avoid using a too long identifier.
                            Base64UrlEncoder.Encode(rsaSecurityKey.Parameters.Modulus)
                                            .Substring(0, 40).ToUpperInvariant(),

                        // Both E and N must be base64url-encoded.
                        // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#appendix-A.1
                        E = Base64UrlEncoder.Encode(rsaSecurityKey.Parameters.Exponent),
                        N = Base64UrlEncoder.Encode(rsaSecurityKey.Parameters.Modulus)
                    });
                }
            }

            await Options.Provider.CryptographyEndpoint(notification);

            if (notification.HandledResponse)
            {
                return;
            }

            var payload = new JObject();
            var keys = new JArray();

            foreach (var key in notification.Keys)
            {
                var item = new JObject();

                // Ensure a key type has been provided.
                // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#section-4.1
                if (string.IsNullOrEmpty(key.Kty))
                {
                    Logger.LogWarning("Cryptography endpoint: a JSON Web Key didn't " +
                        "contain the mandatory 'Kty' parameter and has been ignored.");

                    continue;
                }

                // Create a dictionary associating the
                // JsonWebKey components with their values.
                var parameters = new Dictionary<string, string>
                {
                    [JsonWebKeyParameterNames.Kid] = key.Kid,
                    [JsonWebKeyParameterNames.Use] = key.Use,
                    [JsonWebKeyParameterNames.Kty] = key.Kty,
                    [JsonWebKeyParameterNames.Alg] = key.Alg,
                    [JsonWebKeyParameterNames.X5t] = key.X5t,
                    [JsonWebKeyParameterNames.X5u] = key.X5u,
                    [JsonWebKeyParameterNames.E] = key.E,
                    [JsonWebKeyParameterNames.N] = key.N
                };

                foreach (var parameter in parameters)
                {
                    if (!string.IsNullOrEmpty(parameter.Value))
                    {
                        item.Add(parameter.Key, parameter.Value);
                    }
                }

                if (key.KeyOps.Any())
                {
                    item.Add(JsonWebKeyParameterNames.KeyOps, JArray.FromObject(key.KeyOps));
                }

                if (key.X5c.Any())
                {
                    item.Add(JsonWebKeyParameterNames.X5c, JArray.FromObject(key.X5c));
                }

                keys.Add(item);
            }

            payload.Add(JsonWebKeyParameterNames.Keys, keys);

            var context = new CryptographyEndpointResponseContext(Context, Options, payload);
            await Options.Provider.CryptographyEndpointResponse(context);

            if (context.HandledResponse)
            {
                return;
            }

            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer)))
            {
                payload.WriteTo(writer);
                writer.Flush();

                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Context.RequestAborted);
            }
        }

        private async Task InvokeTokenEndpointAsync()
        {
            if (!string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await SendErrorPayloadAsync(new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed token request has been received: make sure to use POST."
                });

                return;
            }

            // See http://openid.net/specs/openid-connect-core-1_0.html#FormSerialization
            if (string.IsNullOrEmpty(Request.ContentType))
            {
                await SendErrorPayloadAsync(new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed token request has been received: " +
                        "the mandatory 'Content-Type' header was missing from the POST request."
                });

                return;
            }

            // May have media/type; charset=utf-8, allow partial match.
            if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            {
                await SendErrorPayloadAsync(new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed token request has been received: " +
                        "the 'Content-Type' header contained an unexcepted value. " +
                        "Make sure to use 'application/x-www-form-urlencoded'."
                });

                return;
            }

            var form = await Request.ReadFormAsync(Context.RequestAborted);

            var request = new OpenIdConnectMessage(form.ToDictionary())
            {
                RequestType = OpenIdConnectRequestType.TokenRequest
            };

            // Reject token requests missing the mandatory grant_type parameter.
            if (string.IsNullOrEmpty(request.GrantType))
            {
                Logger.LogError("The token request was rejected because the grant type was missing.");

                await SendErrorPayloadAsync(new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "The mandatory 'grant_type' parameter was missing.",
                });

                return;
            }

            // Reject grant_type=authorization_code requests missing the authorization code.
            // See https://tools.ietf.org/html/rfc6749#section-4.1.3
            else if (request.IsAuthorizationCodeGrantType() && string.IsNullOrEmpty(request.Code))
            {
                Logger.LogError("The token request was rejected because the authorization code was missing.");

                await SendErrorPayloadAsync(new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "The mandatory 'code' parameter was missing."
                });

                return;
            }

            // Reject grant_type=refresh_token requests missing the refresh token.
            // See https://tools.ietf.org/html/rfc6749#section-6
            else if (request.IsRefreshTokenGrantType() && string.IsNullOrEmpty(request.RefreshToken))
            {
                Logger.LogError("The token request was rejected because the refresh token was missing.");

                await SendErrorPayloadAsync(new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "The mandatory 'refresh_token' parameter was missing."
                });

                return;
            }

            // Reject grant_type=password requests missing username or password.
            // See https://tools.ietf.org/html/rfc6749#section-4.3.2
            else if (request.IsPasswordGrantType() && (string.IsNullOrEmpty(request.Username) ||
                                                       string.IsNullOrEmpty(request.Password)))
            {
                Logger.LogError("The token request was rejected because the resource owner credentials were missing.");

                await SendErrorPayloadAsync(new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "The mandatory 'username' and/or 'password' parameters " +
                                       "was/were missing from the request message."
                });

                return;
            }

            // When client_id and client_secret are both null, try to extract them from the Authorization header.
            // See http://tools.ietf.org/html/rfc6749#section-2.3.1 and
            // http://openid.net/specs/openid-connect-core-1_0.html#ClientAuthentication
            if (string.IsNullOrEmpty(request.ClientId) && string.IsNullOrEmpty(request.ClientSecret))
            {
                string header = Request.Headers[HeaderNames.Authorization];
                if (!string.IsNullOrEmpty(header) && header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var value = header.Substring("Basic ".Length).Trim();
                        var data = Encoding.UTF8.GetString(Convert.FromBase64String(value));

                        var index = data.IndexOf(':');
                        if (index >= 0)
                        {
                            request.ClientId = data.Substring(0, index);
                            request.ClientSecret = data.Substring(index + 1);
                        }
                    }

                    catch (FormatException) { }
                    catch (ArgumentException) { }
                }
            }

            var clientNotification = new ValidateClientAuthenticationContext(Context, Options, request);
            await Options.Provider.ValidateClientAuthentication(clientNotification);

            // Reject the request if client authentication was rejected.
            if (clientNotification.IsRejected)
            {
                Logger.LogError("invalid client authentication.");

                await SendErrorPayloadAsync(new OpenIdConnectMessage
                {
                    Error = clientNotification.Error ?? OpenIdConnectConstants.Errors.InvalidClient,
                    ErrorDescription = clientNotification.ErrorDescription,
                    ErrorUri = clientNotification.ErrorUri
                });

                return;
            }

            // Reject grant_type=client_credentials requests if client authentication was skipped.
            if (clientNotification.IsSkipped && request.IsClientCredentialsGrantType())
            {
                Logger.LogError("client authentication is required for client_credentials grant type.");

                await SendErrorPayloadAsync(new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.InvalidGrant,
                    ErrorDescription = "client authentication is required when using client_credentials"
                });

                return;
            }

            var validatingContext = new ValidateTokenRequestContext(Context, Options, request);

            // Validate the token request immediately if the grant type used by
            // the client application doesn't rely on a previously-issued token/code.
            if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
            {
                await Options.Provider.ValidateTokenRequest(validatingContext);

                if (!validatingContext.IsValidated)
                {
                    // Note: use invalid_request as the default error if none has been explicitly provided.
                    await SendErrorPayloadAsync(new OpenIdConnectMessage
                    {
                        Error = validatingContext.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = validatingContext.ErrorDescription,
                        ErrorUri = validatingContext.ErrorUri
                    });

                    return;
                }
            }

            AuthenticationTicket ticket = null;

            // See http://tools.ietf.org/html/rfc6749#section-4.1
            // and http://tools.ietf.org/html/rfc6749#section-4.1.3 (authorization code grant).
            // See http://tools.ietf.org/html/rfc6749#section-6 (refresh token grant).
            if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
            {
                ticket = request.IsAuthorizationCodeGrantType() ?
                    await DeserializeAuthorizationCodeAsync(request.Code, request) :
                    await DeserializeRefreshTokenAsync(request.RefreshToken, request);

                if (ticket == null)
                {
                    Logger.LogError("invalid ticket");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage
                    {
                        Error = OpenIdConnectConstants.Errors.InvalidGrant,
                        ErrorDescription = "Invalid ticket"
                    });

                    return;
                }

                if (!ticket.Properties.ExpiresUtc.HasValue ||
                     ticket.Properties.ExpiresUtc < Options.SystemClock.UtcNow)
                {
                    Logger.LogError("expired ticket");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage
                    {
                        Error = OpenIdConnectConstants.Errors.InvalidGrant,
                        ErrorDescription = "Expired ticket"
                    });

                    return;
                }

                // If the client was fully authenticated when retrieving its refresh token,
                // the current request must be rejected if client authentication was not enforced.
                if (request.IsRefreshTokenGrantType() && !clientNotification.IsValidated && ticket.IsConfidential())
                {
                    Logger.LogError("client authentication is required to use this ticket");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage
                    {
                        Error = OpenIdConnectConstants.Errors.InvalidGrant,
                        ErrorDescription = "Client authentication is required to use this ticket"
                    });

                    return;
                }

                // Note: identifier may be null during a grant_type=refresh_token request if the refresh token
                // was issued to a public client but cannot be null for an authorization code grant request.
                var identifier = ticket.Properties.GetProperty(OpenIdConnectConstants.Extra.ClientId);
                if (request.IsAuthorizationCodeGrantType() && string.IsNullOrEmpty(identifier))
                {
                    Logger.LogError("The client the authorization code was issued to cannot be resolved.");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage
                    {
                        Error = OpenIdConnectConstants.Errors.ServerError,
                        ErrorDescription = "An internal server error occurred."
                    });

                    return;
                }

                // At this stage, client_id cannot be null for grant_type=authorization_code requests,
                // as it must either be set in the ValidateClientAuthentication notification
                // by the developer or manually flowed by non-confidential client applications.
                // See https://tools.ietf.org/html/rfc6749#section-4.1.3
                if (request.IsAuthorizationCodeGrantType() && string.IsNullOrEmpty(request.ClientId))
                {
                    Logger.LogError("client_id was missing from the token request");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage
                    {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "client_id was missing from the token request"
                    });

                    return;
                }

                // Ensure the authorization code/refresh token was issued to the client application making the token request.
                // Note: when using the refresh token grant, client_id is optional but must validated if present.
                // As a consequence, this check doesn't depend on the actual status of client authentication.
                // See https://tools.ietf.org/html/rfc6749#section-6
                // and http://openid.net/specs/openid-connect-core-1_0.html#RefreshingAccessToken
                if (!string.IsNullOrEmpty(identifier) && !string.IsNullOrEmpty(request.ClientId) &&
                    !string.Equals(identifier, request.ClientId, StringComparison.Ordinal))
                {
                    Logger.LogError("ticket does not contain matching client_id");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage
                    {
                        Error = OpenIdConnectConstants.Errors.InvalidGrant,
                        ErrorDescription = "Ticket does not contain matching client_id"
                    });

                    return;
                }

                // Validate the redirect_uri flowed by the client application during this token request.
                // Note: for pure OAuth2 requests, redirect_uri is only mandatory if the authorization request
                // contained an explicit redirect_uri. OpenID Connect requests MUST include a redirect_uri
                // but the specifications allow proceeding the token request without returning an error
                // if the authorization request didn't contain an explicit redirect_uri.
                // See https://tools.ietf.org/html/rfc6749#section-4.1.3
                // and http://openid.net/specs/openid-connect-core-1_0.html#TokenRequestValidation
                string address;
                if (request.IsAuthorizationCodeGrantType() &&
                    ticket.Properties.Items.TryGetValue(OpenIdConnectConstants.Extra.RedirectUri, out address))
                {
                    ticket.Properties.Items.Remove(OpenIdConnectConstants.Extra.RedirectUri);

                    if (string.IsNullOrEmpty(request.RedirectUri))
                    {
                        Logger.LogError("redirect_uri was missing from the grant_type=authorization_code request.");

                        await SendErrorPayloadAsync(new OpenIdConnectMessage
                        {
                            Error = OpenIdConnectConstants.Errors.InvalidRequest,
                            ErrorDescription = "redirect_uri was missing from the token request"
                        });

                        return;
                    }

                    else if (!string.Equals(address, request.RedirectUri, StringComparison.Ordinal))
                    {
                        Logger.LogError("authorization code does not contain matching redirect_uri");

                        await SendErrorPayloadAsync(new OpenIdConnectMessage
                        {
                            Error = OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = "Authorization code does not contain matching redirect_uri"
                        });

                        return;
                    }
                }

                if (!string.IsNullOrEmpty(request.Resource))
                {
                    // When an explicit resource parameter has been included in the token request
                    // but was missing from the authorization request, the request MUST be rejected.
                    var resources = ticket.Properties.GetResources();
                    if (!resources.Any())
                    {
                        Logger.LogError("token request cannot contain a resource");

                        await SendErrorPayloadAsync(new OpenIdConnectMessage
                        {
                            Error = OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = "Token request cannot contain a resource parameter" +
                                               "if the authorization request didn't contain one"
                        });

                        return;
                    }

                    // When an explicit resource parameter has been included in the token request,
                    // the authorization server MUST ensure that it doesn't contain resources
                    // that were not allowed during the authorization request.
                    else if (!resources.ContainsSet(request.GetResources()))
                    {
                        Logger.LogError("token request does not contain matching resource");

                        await SendErrorPayloadAsync(new OpenIdConnectMessage
                        {
                            Error = OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = "Token request doesn't contain a valid resource parameter"
                        });

                        return;
                    }

                    // Remove the "resource" property from the authentication ticket corresponding
                    // to the authorization code/refresh token to force the token endpoint
                    // to use the "resource" parameter flowed in the token request.
                    ticket.Properties.Items.Remove(OpenIdConnectConstants.Extra.Resource);
                }

                else {
                    // When no explicit "resource" parameter has been received, the "resource" parameter sent
                    // during the authorization request or the previous token request is used instead.
                    request.Resource = ticket.GetProperty(OpenIdConnectConstants.Extra.Resource);
                }

                if (!string.IsNullOrEmpty(request.Scope))
                {
                    // When an explicit scope parameter has been included in the token request
                    // but was missing from the authorization request, the request MUST be rejected.
                    // See http://tools.ietf.org/html/rfc6749#section-6
                    var scopes = ticket.Properties.GetScopes();
                    if (!scopes.Any())
                    {
                        Logger.LogError("token request cannot contain a scope");

                        await SendErrorPayloadAsync(new OpenIdConnectMessage
                        {
                            Error = OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = "Token request cannot contain a scope parameter" +
                                               "if the authorization request didn't contain one"
                        });

                        return;
                    }

                    // When an explicit scope parameter has been included in the token request,
                    // the authorization server MUST ensure that it doesn't contain scopes
                    // that were not allowed during the authorization request.
                    // See https://tools.ietf.org/html/rfc6749#section-6
                    else if (!scopes.ContainsSet(request.GetScopes()))
                    {
                        Logger.LogError("authorization code does not contain matching scope");

                        await SendErrorPayloadAsync(new OpenIdConnectMessage
                        {
                            Error = OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = "Token request doesn't contain a valid scope parameter"
                        });

                        return;
                    }

                    // Remove the "scope" property from the authentication ticket corresponding
                    // to the authorization code/refresh token to force the token endpoint
                    // to use the "scope" parameter flowed in the token request.
                    ticket.Properties.Items.Remove(OpenIdConnectConstants.Extra.Scope);
                }

                else {
                    // When no explicit "scope" parameter has been received, the "scope" parameter sent
                    // during the authorization request or the previous token request is used instead.
                    request.Scope = ticket.GetProperty(OpenIdConnectConstants.Extra.Scope);
                }

                // Expose the authentication ticket extracted from the authorization
                // code or the refresh token before invoking ValidateTokenRequest.
                validatingContext.AuthenticationTicket = ticket;

                await Options.Provider.ValidateTokenRequest(validatingContext);

                if (!validatingContext.IsValidated)
                {
                    // Note: use invalid_request as the default error if none has been explicitly provided.
                    await SendErrorPayloadAsync(new OpenIdConnectMessage
                    {
                        Error = validatingContext.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = validatingContext.ErrorDescription,
                        ErrorUri = validatingContext.ErrorUri
                    });

                    return;
                }

                if (request.IsAuthorizationCodeGrantType())
                {
                    // Note: the authentication ticket is copied to avoid modifying the properties of the authorization code.
                    var context = new GrantAuthorizationCodeContext(Context, Options, request, ticket.Copy());
                    await Options.Provider.GrantAuthorizationCode(context);

                    if (!context.IsValidated)
                    {
                        // Note: use invalid_grant as the default error if none has been explicitly provided.
                        await SendErrorPayloadAsync(new OpenIdConnectMessage
                        {
                            Error = context.Error ?? OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = context.ErrorDescription,
                            ErrorUri = context.ErrorUri
                        });

                        return;
                    }

                    ticket = context.AuthenticationTicket;
                }

                else {
                    // Note: the authentication ticket is copied to avoid modifying the properties of the refresh token.
                    var context = new GrantRefreshTokenContext(Context, Options, request, ticket.Copy());
                    await Options.Provider.GrantRefreshToken(context);

                    if (!context.IsValidated)
                    {
                        // Note: use invalid_grant as the default error if none has been explicitly provided.
                        await SendErrorPayloadAsync(new OpenIdConnectMessage
                        {
                            Error = context.Error ?? OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = context.ErrorDescription,
                            ErrorUri = context.ErrorUri
                        });

                        return;
                    }

                    ticket = context.AuthenticationTicket;
                }

                // By default, when using the authorization code or the refresh token grants, the authentication ticket
                // extracted from the code/token is used as-is. If the developer didn't provide his own ticket
                // or didn't set an explicit expiration date, the ticket properties are reset to avoid aligning the
                // expiration date of the generated tokens with the lifetime of the authorization code/refresh token.
                if (ticket.Properties.IssuedUtc == validatingContext.AuthenticationTicket.Properties.IssuedUtc)
                {
                    ticket.Properties.IssuedUtc = null;
                }

                if (ticket.Properties.ExpiresUtc == validatingContext.AuthenticationTicket.Properties.ExpiresUtc)
                {
                    ticket.Properties.ExpiresUtc = null;
                }
            }

            // See http://tools.ietf.org/html/rfc6749#section-4.3
            // and http://tools.ietf.org/html/rfc6749#section-4.3.2
            else if (request.IsPasswordGrantType())
            {
                var context = new GrantResourceOwnerCredentialsContext(Context, Options, request);
                await Options.Provider.GrantResourceOwnerCredentials(context);

                if (!context.IsValidated)
                {
                    // Note: use invalid_grant as the default error if none has been explicitly provided.
                    await SendErrorPayloadAsync(new OpenIdConnectMessage
                    {
                        Error = context.Error ?? OpenIdConnectConstants.Errors.InvalidGrant,
                        ErrorDescription = context.ErrorDescription,
                        ErrorUri = context.ErrorUri
                    });

                    return;
                }

                ticket = context.AuthenticationTicket;
            }

            // See http://tools.ietf.org/html/rfc6749#section-4.4
            // and http://tools.ietf.org/html/rfc6749#section-4.4.2
            else if (request.IsClientCredentialsGrantType())
            {
                var context = new GrantClientCredentialsContext(Context, Options, request);
                await Options.Provider.GrantClientCredentials(context);

                if (!context.IsValidated)
                {
                    // Note: use unauthorized_client as the default error if none has been explicitly provided.
                    await SendErrorPayloadAsync(new OpenIdConnectMessage
                    {
                        Error = context.Error ?? OpenIdConnectConstants.Errors.UnauthorizedClient,
                        ErrorDescription = context.ErrorDescription,
                        ErrorUri = context.ErrorUri
                    });

                    return;
                }

                ticket = context.AuthenticationTicket;
            }

            // See http://tools.ietf.org/html/rfc6749#section-8.3
            else {
                var context = new GrantCustomExtensionContext(Context, Options, request);
                await Options.Provider.GrantCustomExtension(context);

                if (!context.IsValidated)
                {
                    // Note: use unsupported_grant_type as the default error if none has been explicitly provided.
                    await SendErrorPayloadAsync(new OpenIdConnectMessage
                    {
                        Error = context.Error ?? OpenIdConnectConstants.Errors.UnsupportedGrantType,
                        ErrorDescription = context.ErrorDescription,
                        ErrorUri = context.ErrorUri
                    });

                    return;
                }

                ticket = context.AuthenticationTicket;
            }

            var notification = new TokenEndpointContext(Context, Options, request, ticket);
            await Options.Provider.TokenEndpoint(notification);

            if (notification.HandledResponse)
            {
                return;
            }

            // Flow the changes made to the ticket.
            ticket = notification.Ticket;

            // Ensure an authentication ticket has been provided:
            // a null ticket MUST result in an internal server error.
            if (ticket == null)
            {
                Logger.LogError("authentication ticket missing");

                await SendErrorPayloadAsync(new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.ServerError
                });

                return;
            }

            if (!string.IsNullOrEmpty(request.ClientId))
            {
                // Keep the original client_id parameter for later comparison.
                ticket.Properties.Items[OpenIdConnectConstants.Extra.ClientId] = request.ClientId;
            }

            if (clientNotification.IsValidated)
            {
                // Store a boolean indicating whether the ticket should be marked as confidential.
                ticket.Properties.Items[OpenIdConnectConstants.Extra.Confidential] = "true";
            }

            // Note: the application is allowed to specify a different "resource": in this case,
            // don't replace the "resource" property stored in the authentication ticket.
            if (!string.IsNullOrEmpty(request.Resource) &&
                !ticket.Properties.Items.ContainsKey(OpenIdConnectConstants.Extra.Resource))
            {
                // Keep the original resource parameter for later comparison.
                ticket.Properties.Items[OpenIdConnectConstants.Extra.Resource] = request.Resource;
            }

            // Note: the application is allowed to specify a different "scope": in this case,
            // don't replace the "scope" property stored in the authentication ticket.
            if (!string.IsNullOrEmpty(request.Scope) &&
                !ticket.Properties.Items.ContainsKey(OpenIdConnectConstants.Extra.Scope))
            {
                // Keep the original scope parameter for later comparison.
                ticket.Properties.Items[OpenIdConnectConstants.Extra.Scope] = request.Scope;
            }

            var response = new OpenIdConnectMessage();

            // Note: by default, an access token is always returned, but the client application can use the "response_type" parameter
            // to only include specific types of tokens. When this parameter is missing, an access token is always generated.
            if (string.IsNullOrEmpty(request.ResponseType) || request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.Token))
            {
                // Make sure to create a copy of the authentication properties
                // to avoid modifying the properties set on the original ticket.
                var properties = ticket.Properties.Copy();

                // Note: when the "resource" parameter added to the OpenID Connect response
                // is identical to the request parameter, keeping it is not necessary.
                var resource = properties.GetProperty(OpenIdConnectConstants.Extra.Resource);
                if (request.IsAuthorizationCodeGrantType() || !string.Equals(request.Resource, resource, StringComparison.Ordinal))
                {
                    response.Resource = resource;
                }

                // Note: when the "scope" parameter added to the OpenID Connect response
                // is identical to the request parameter, keeping it is not necessary.
                var scope = properties.GetProperty(OpenIdConnectConstants.Extra.Scope);
                if (request.IsAuthorizationCodeGrantType() || !string.Equals(request.Scope, scope, StringComparison.Ordinal))
                {
                    response.Scope = scope;
                }

                // When sliding expiration is disabled, the access token added to the response
                // cannot live longer than the refresh token that was used in the token request.
                if (request.IsRefreshTokenGrantType() && !Options.UseSlidingExpiration &&
                    validatingContext.AuthenticationTicket.Properties.ExpiresUtc.HasValue &&
                    validatingContext.AuthenticationTicket.Properties.ExpiresUtc.Value <
                        (Options.SystemClock.UtcNow + Options.AccessTokenLifetime))
                {
                    properties.ExpiresUtc = validatingContext.AuthenticationTicket.Properties.ExpiresUtc;
                }

                response.TokenType = OpenIdConnectConstants.TokenTypes.Bearer;
                response.AccessToken = await SerializeAccessTokenAsync(ticket.Principal, properties, request, response);

                // Ensure that an access token is issued to avoid returning an invalid response.
                // See http://openid.net/specs/oauth-v2-multiple-response-types-1_0.html#Combinations
                if (string.IsNullOrEmpty(response.AccessToken))
                {
                    Logger.LogError("SerializeAccessTokenAsync returned no access token.");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage
                    {
                        Error = OpenIdConnectConstants.Errors.ServerError,
                        ErrorDescription = "no valid access token was issued"
                    });

                    return;
                }

                // properties.ExpiresUtc is automatically set by SerializeAccessTokenAsync but the end user
                // is free to set a null value directly in the SerializeAccessToken event.
                if (properties.ExpiresUtc.HasValue && properties.ExpiresUtc > Options.SystemClock.UtcNow)
                {
                    var lifetime = properties.ExpiresUtc.Value - Options.SystemClock.UtcNow;
                    var expiration = (long)(lifetime.TotalSeconds + .5);

                    response.ExpiresIn = expiration.ToString(CultureInfo.InvariantCulture);
                }
            }

            // Note: by default, an identity token is always returned when the "openid" scope has been requested,
            // but the client application can use the "response_type" parameter to only include specific types of tokens.
            // When this parameter is missing, an identity token is always generated.
            if (request.ContainsScope(OpenIdConnectConstants.Scopes.OpenId) && (string.IsNullOrEmpty(request.ResponseType) ||
                                                                                request.ContainsResponseType("id_token")))
            {
                // Make sure to create a copy of the authentication properties
                // to avoid modifying the properties set on the original ticket.
                var properties = ticket.Properties.Copy();

                // When sliding expiration is disabled, the identity token added to the response
                // cannot live longer than the refresh token that was used in the token request.
                if (request.IsRefreshTokenGrantType() && !Options.UseSlidingExpiration &&
                    validatingContext.AuthenticationTicket.Properties.ExpiresUtc.HasValue &&
                    validatingContext.AuthenticationTicket.Properties.ExpiresUtc.Value <
                        (Options.SystemClock.UtcNow + Options.IdentityTokenLifetime))
                {
                    properties.ExpiresUtc = validatingContext.AuthenticationTicket.Properties.ExpiresUtc;
                }

                response.IdToken = await SerializeIdentityTokenAsync(ticket.Principal, properties, request, response);

                // Ensure that an identity token is issued to avoid returning an invalid response.
                // See http://openid.net/specs/openid-connect-core-1_0.html#TokenResponse
                // and http://openid.net/specs/openid-connect-core-1_0.html#RefreshTokenResponse
                if (string.IsNullOrEmpty(response.IdToken))
                {
                    Logger.LogError("SerializeIdentityTokenAsync returned no identity token.");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage
                    {
                        Error = OpenIdConnectConstants.Errors.ServerError,
                        ErrorDescription = "no valid identity token was issued"
                    });

                    return;
                }
            }

            // Note: by default, a refresh token is always returned when the "offline_access" scope has been requested,
            // but the client application can use the "response_type" parameter to only include specific types of tokens.
            // When this parameter is missing, a refresh token is always generated.
            if (request.ContainsScope(OpenIdConnectConstants.Scopes.OfflineAccess) && (string.IsNullOrEmpty(request.ResponseType) ||
                                                                                       request.ContainsResponseType("refresh_token")))
            {
                // Make sure to create a copy of the authentication properties
                // to avoid modifying the properties set on the original ticket.
                var properties = ticket.Properties.Copy();

                // When sliding expiration is disabled, the refresh token added to the response
                // cannot live longer than the refresh token that was used in the token request.
                if (request.IsRefreshTokenGrantType() && !Options.UseSlidingExpiration &&
                    validatingContext.AuthenticationTicket.Properties.ExpiresUtc.HasValue &&
                    validatingContext.AuthenticationTicket.Properties.ExpiresUtc.Value <
                        (Options.SystemClock.UtcNow + Options.RefreshTokenLifetime))
                {
                    properties.ExpiresUtc = validatingContext.AuthenticationTicket.Properties.ExpiresUtc;
                }

                response.RefreshToken = await SerializeRefreshTokenAsync(ticket.Principal, properties, request, response);
            }

            var payload = new JObject();

            foreach (var parameter in response.Parameters)
            {
                payload.Add(parameter.Key, parameter.Value);
            }

            var responseNotification = new TokenEndpointResponseContext(Context, Options, ticket, request, payload);
            await Options.Provider.TokenEndpointResponse(responseNotification);

            if (responseNotification.HandledResponse)
            {
                return;
            }

            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer)))
            {
                payload.WriteTo(writer);
                writer.Flush();

                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                Response.Headers[HeaderNames.CacheControl] = "no-cache";
                Response.Headers[HeaderNames.Pragma] = "no-cache";
                Response.Headers[HeaderNames.Expires] = "-1";

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Context.RequestAborted);
            }
        }

        private async Task<bool> InvokeProfileEndpointAsync()
        {
            OpenIdConnectMessage request;

            if (!string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await SendErrorPayloadAsync(new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed userinfo request has been received: " +
                        "make sure to use either GET or POST."
                });

                return true;
            }

            if (string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                request = new OpenIdConnectMessage(Request.Query.ToDictionary());
            }

            else {
                // See http://openid.net/specs/openid-connect-core-1_0.html#FormSerialization
                if (string.IsNullOrWhiteSpace(Request.ContentType))
                {
                    await SendErrorPayloadAsync(new OpenIdConnectMessage
                    {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed userinfo request has been received: " +
                            "the mandatory 'Content-Type' header was missing from the POST request."
                    });

                    return true;
                }

                // May have media/type; charset=utf-8, allow partial match.
                if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                {
                    await SendErrorPayloadAsync(new OpenIdConnectMessage
                    {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed userinfo request has been received: " +
                            "the 'Content-Type' header contained an unexcepted value. " +
                            "Make sure to use 'application/x-www-form-urlencoded'."
                    });

                    return true;
                }

                var form = await Request.ReadFormAsync(Context.RequestAborted);

                request = new OpenIdConnectMessage(form.ToDictionary());
            }

            string token;
            if (!string.IsNullOrEmpty(request.AccessToken))
            {
                token = request.AccessToken;
            }

            else {
                string header = Request.Headers[HeaderNames.Authorization];
                if (string.IsNullOrEmpty(header))
                {
                    await SendErrorPayloadAsync(new OpenIdConnectMessage
                    {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed userinfo request has been received."
                    });

                    return true;
                }

                if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    await SendErrorPayloadAsync(new OpenIdConnectMessage
                    {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed userinfo request has been received."
                    });

                    return true;
                }

                token = header.Substring("Bearer ".Length);
                if (string.IsNullOrEmpty(token))
                {
                    await SendErrorPayloadAsync(new OpenIdConnectMessage
                    {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed userinfo request has been received."
                    });

                    return true;
                }
            }

            var ticket = await DeserializeAccessTokenAsync(token, request);
            if (ticket == null)
            {
                Logger.LogError("invalid token");

                // Note: an invalid token should result in an unauthorized response
                // but returning a 401 status would invoke the previously registered
                // authentication middleware and potentially replace it by a 302 response.
                // To work around this limitation, a 400 error is returned instead.
                // See http://openid.net/specs/openid-connect-core-1_0.html#UserInfoError
                await SendErrorPayloadAsync(new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.InvalidGrant,
                    ErrorDescription = "Invalid token."
                });

                return true;
            }

            if (!ticket.Properties.ExpiresUtc.HasValue ||
                 ticket.Properties.ExpiresUtc < Options.SystemClock.UtcNow)
            {
                Logger.LogError("expired token");

                // Note: an invalid token should result in an unauthorized response
                // but returning a 401 status would invoke the previously registered
                // authentication middleware and potentially replace it by a 302 response.
                // To work around this limitation, a 400 error is returned instead.
                // See http://openid.net/specs/openid-connect-core-1_0.html#UserInfoError
                await SendErrorPayloadAsync(new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.InvalidGrant,
                    ErrorDescription = "Expired token."
                });

                return true;
            }

            // Insert the userinfo request in the ASP.NET context.
            Context.SetOpenIdConnectRequest(request);

            var notification = new ProfileEndpointContext(Context, Options, request, ticket);

            // 'sub' is a mandatory claim but is not necessarily present as-is: when missing,
            // the name identifier extracted from the authentication ticket is used instead.
            // See http://openid.net/specs/openid-connect-core-1_0.html#UserInfoResponse
            notification.Subject = ticket.Principal.GetClaim(JwtRegisteredClaimNames.Sub) ??
                                   ticket.Principal.GetClaim(ClaimTypes.NameIdentifier);

            notification.Audience = ticket.Principal.GetClaim(JwtRegisteredClaimNames.Azp);

            notification.Issuer = Context.GetIssuer(Options);

            // The following claims are all optional and should be excluded when
            // no corresponding value has been found in the authentication ticket.
            if (ticket.ContainsScope(OpenIdConnectConstants.Scopes.Profile))
            {
                notification.FamilyName = ticket.Principal.GetClaim(ClaimTypes.Surname);
                notification.GivenName = ticket.Principal.GetClaim(ClaimTypes.GivenName);
                notification.BirthDate = ticket.Principal.GetClaim(ClaimTypes.DateOfBirth);
            }

            if (ticket.ContainsScope(OpenIdConnectConstants.Scopes.Email))
            {
                notification.Email = ticket.Principal.GetClaim(ClaimTypes.Email);
            };

            if (ticket.ContainsScope(OpenIdConnectConstants.Scopes.Phone))
            {
                notification.PhoneNumber = ticket.Principal.GetClaim(ClaimTypes.HomePhone) ??
                                           ticket.Principal.GetClaim(ClaimTypes.MobilePhone) ??
                                           ticket.Principal.GetClaim(ClaimTypes.OtherPhone);
            };

            await Options.Provider.ProfileEndpoint(notification);

            if (notification.HandledResponse)
            {
                return true;
            }

            else if (notification.Skipped)
            {
                return false;
            }

            // Ensure the "sub" claim has been correctly populated.
            if (string.IsNullOrEmpty(notification.Subject))
            {
                Logger.LogError("The mandatory 'sub' claim was missing from the userinfo response.");

                Response.StatusCode = 500;

                await SendErrorPayloadAsync(new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.ServerError,
                    ErrorDescription = "The mandatory 'sub' claim was missing."
                });

                return true;
            }

            var payload = new JObject
            {
                [JwtRegisteredClaimNames.Sub] = notification.Subject
            };

            if (notification.Address != null)
            {
                payload[OpenIdConnectConstants.Claims.Address] = notification.Address;
            }

            if (!string.IsNullOrEmpty(notification.Audience))
            {
                payload[JwtRegisteredClaimNames.Aud] = notification.Audience;
            }

            if (!string.IsNullOrEmpty(notification.BirthDate))
            {
                payload[JwtRegisteredClaimNames.Birthdate] = notification.BirthDate;
            }

            if (!string.IsNullOrEmpty(notification.Email))
            {
                payload[JwtRegisteredClaimNames.Email] = notification.Email;
            }

            if (notification.EmailVerified.HasValue)
            {
                payload[OpenIdConnectConstants.Claims.EmailVerified] = notification.EmailVerified.Value;
            }

            if (!string.IsNullOrEmpty(notification.FamilyName))
            {
                payload[JwtRegisteredClaimNames.FamilyName] = notification.FamilyName;
            }

            if (!string.IsNullOrEmpty(notification.GivenName))
            {
                payload[JwtRegisteredClaimNames.GivenName] = notification.GivenName;
            }

            if (!string.IsNullOrEmpty(notification.Issuer))
            {
                payload[JwtRegisteredClaimNames.Iss] = notification.Issuer;
            }

            if (!string.IsNullOrEmpty(notification.PhoneNumber))
            {
                payload[OpenIdConnectConstants.Claims.PhoneNumber] = notification.PhoneNumber;
            }

            if (notification.PhoneNumberVerified.HasValue)
            {
                payload[OpenIdConnectConstants.Claims.PhoneNumberVerified] = notification.PhoneNumberVerified.Value;
            }

            if (!string.IsNullOrEmpty(notification.PreferredUsername))
            {
                payload[OpenIdConnectConstants.Claims.PreferredUsername] = notification.PreferredUsername;
            }

            if (!string.IsNullOrEmpty(notification.Profile))
            {
                payload[OpenIdConnectConstants.Claims.Profile] = notification.Profile;
            }

            if (!string.IsNullOrEmpty(notification.Website))
            {
                payload[OpenIdConnectConstants.Claims.Website] = notification.Website;
            }

            foreach (var claim in notification.Claims)
            {
                // Ignore claims whose value is null.
                if (claim.Value == null)
                {
                    continue;
                }

                payload.Add(claim.Key, claim.Value);
            }

            var context = new ProfileEndpointResponseContext(Context, Options, request, payload);
            await Options.Provider.ProfileEndpointResponse(context);

            if (context.HandledResponse)
            {
                return true;
            }

            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer)))
            {
                payload.WriteTo(writer);
                writer.Flush();

                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                Response.Headers[HeaderNames.CacheControl] = "no-cache";
                Response.Headers[HeaderNames.Pragma] = "no-cache";
                Response.Headers[HeaderNames.Expires] = "-1";

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Context.RequestAborted);
            }

            return true;
        }

        private async Task InvokeValidationEndpointAsync()
        {
            OpenIdConnectMessage request;

            // See https://tools.ietf.org/html/rfc7662#section-2.1
            // and https://tools.ietf.org/html/rfc7662#section-4
            if (!string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await SendErrorPayloadAsync(new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed validation request has been received: " +
                        "make sure to use either GET or POST."
                });

                return;
            }

            if (string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                request = new OpenIdConnectMessage(Request.Query.ToDictionary())
                {
                    RequestType = OpenIdConnectRequestType.AuthenticationRequest
                };
            }

            else {
                // See http://openid.net/specs/openid-connect-core-1_0.html#FormSerialization
                if (string.IsNullOrEmpty(Request.ContentType))
                {
                    await SendErrorPayloadAsync(new OpenIdConnectMessage
                    {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed validation request has been received: " +
                            "the mandatory 'Content-Type' header was missing from the POST request."
                    });

                    return;
                }

                // May have media/type; charset=utf-8, allow partial match.
                if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                {
                    await SendErrorPayloadAsync(new OpenIdConnectMessage
                    {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed validation request has been received: " +
                            "the 'Content-Type' header contained an unexcepted value. " +
                            "Make sure to use 'application/x-www-form-urlencoded'."
                    });

                    return;
                }

                var form = await Request.ReadFormAsync(Context.RequestAborted);

                request = new OpenIdConnectMessage(form.ToDictionary())
                {
                    RequestType = OpenIdConnectRequestType.AuthenticationRequest
                };
            }

            if (string.IsNullOrWhiteSpace(request.GetToken()))
            {
                await SendErrorPayloadAsync(new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed validation request has been received: " +
                        "a 'token' parameter with an access, refresh, or identity token is required."
                });

                return;
            }

            var clientNotification = new ValidateClientAuthenticationContext(Context, Options, request);
            await Options.Provider.ValidateClientAuthentication(clientNotification);

            // Reject the request if client authentication was rejected.
            if (clientNotification.IsRejected)
            {
                Logger.LogError("The validation request was rejected " +
                                "because client authentication was invalid.");

                await SendPayloadAsync(new JObject
                {
                    [OpenIdConnectConstants.Claims.Active] = false
                });

                return;
            }

            AuthenticationTicket ticket = null;

            // Note: use the "token_type_hint" parameter to determine
            // the type of the token sent by the client application.
            // See https://tools.ietf.org/html/rfc7662#section-2.1
            switch (request.GetTokenTypeHint())
            {
                case OpenIdConnectConstants.Usages.AccessToken:
                    ticket = await DeserializeAccessTokenAsync(request.GetToken(), request);
                    break;

                case OpenIdConnectConstants.Usages.RefreshToken:
                    ticket = await DeserializeRefreshTokenAsync(request.GetToken(), request);
                    break;

                case OpenIdConnectConstants.Usages.IdToken:
                    ticket = await DeserializeIdentityTokenAsync(request.GetToken(), request);
                    break;
            }

            // Note: if the token can't be found using "token_type_hint",
            // extend the search across all of the supported token types.
            // See https://tools.ietf.org/html/rfc7662#section-2.1
            if (ticket == null)
            {
                ticket = await DeserializeAccessTokenAsync(request.GetToken(), request) ??
                         await DeserializeIdentityTokenAsync(request.GetToken(), request) ??
                         await DeserializeRefreshTokenAsync(request.GetToken(), request);
            }

            if (ticket == null)
            {
                Logger.LogInformation("The validation request was rejected because the token was invalid.");

                await SendPayloadAsync(new JObject
                {
                    [OpenIdConnectConstants.Claims.Active] = false
                });

                return;
            }

            // Note: unlike refresh or identity tokens that can only be validated by client applications,
            // access tokens can be validated by either resource servers or client applications:
            // in both cases, the caller must be authenticated if the ticket is marked as confidential.
            if (clientNotification.IsSkipped && ticket.IsConfidential())
            {
                Logger.LogWarning("The validation request was rejected " +
                                  "because the caller was not authenticated.");

                await SendPayloadAsync(new JObject
                {
                    [OpenIdConnectConstants.Claims.Active] = false
                });

                return;
            }

            // If the ticket is already expired, directly return active=false.
            if (ticket.Properties.ExpiresUtc.HasValue &&
                ticket.Properties.ExpiresUtc < Options.SystemClock.UtcNow)
            {
                Logger.LogVerbose("expired token");

                await SendPayloadAsync(new JObject
                {
                    [OpenIdConnectConstants.Claims.Active] = false
                });

                return;
            }

            if (ticket.IsAccessToken())
            {
                // When the caller is authenticated, ensure it is
                // listed as a valid audience or authorized party.
                var audiences = ticket.GetAudiences();
                if (clientNotification.IsValidated && !audiences.Contains(clientNotification.ClientId, StringComparer.Ordinal) &&
                                                      !ticket.Principal.HasClaim(JwtRegisteredClaimNames.Azp, clientNotification.ClientId))
                {
                    Logger.LogWarning("The validation request was rejected because the access token " +
                                      "was issued to a different client or for another resource server.");

                    await SendPayloadAsync(new JObject
                    {
                        [OpenIdConnectConstants.Claims.Active] = false
                    });

                    return;
                }
            }

            else if (ticket.IsIdentityToken())
            {
                // When the caller is authenticated, reject the validation
                // request if the caller is not listed as a valid audience.
                var audiences = ticket.GetAudiences();
                if (clientNotification.IsValidated && !audiences.Contains(clientNotification.ClientId, StringComparer.Ordinal))
                {
                    Logger.LogWarning("The validation request was rejected because the " +
                                      "identity token was issued to a different client.");

                    await SendPayloadAsync(new JObject
                    {
                        [OpenIdConnectConstants.Claims.Active] = false
                    });

                    return;
                }
            }

            else if (ticket.IsRefreshToken())
            {
                // When the caller is authenticated, reject the validation request if the caller
                // doesn't correspond to the client application the token was issued to.
                var identifier = ticket.GetProperty(OpenIdConnectConstants.Extra.ClientId);
                if (clientNotification.IsValidated && !string.Equals(identifier, clientNotification.ClientId, StringComparison.Ordinal))
                {
                    Logger.LogWarning("The validation request was rejected because the " +
                                      "refresh token was issued to a different client.");

                    await SendPayloadAsync(new JObject
                    {
                        [OpenIdConnectConstants.Claims.Active] = false
                    });

                    return;
                }
            }

            // Insert the validation request in the ASP.NET context.
            Context.SetOpenIdConnectRequest(request);

            var notification = new ValidationEndpointContext(Context, Options, request, ticket);
            notification.Active = true;

            // Note: "token_type" may be null when the received token is not an access token.
            // See https://tools.ietf.org/html/rfc7662#section-2.2 and https://tools.ietf.org/html/rfc6749#section-5.1
            notification.TokenType = ticket.Principal.GetClaim(OpenIdConnectConstants.Claims.TokenType);

            // Try to resolve the issuer from the "iss" claim extracted from the token.
            // If none can be found, a generic value is determined from the value
            // registered in the options or from the current URL.
            notification.Issuer = ticket.Principal.GetClaim(JwtRegisteredClaimNames.Iss) ??
                                  Context.GetIssuer(Options);

            notification.Subject = ticket.Principal.GetClaim(JwtRegisteredClaimNames.Sub) ??
                                   ticket.Principal.GetClaim(ClaimTypes.NameIdentifier);

            notification.IssuedAt = ticket.Properties.IssuedUtc;
            notification.ExpiresAt = ticket.Properties.ExpiresUtc;

            // Copy the audiences extracted from the "aud" claim.
            foreach (var audience in ticket.GetAudiences())
            {
                notification.Audiences.Add(audience);
            }

            // Note: non-metadata claims are only added if the caller is authenticated AND is in the specified audiences.
            if (clientNotification.IsValidated && notification.Audiences.Contains(clientNotification.ClientId))
            {
                // Extract the main identity associated with the principal.
                var identity = (ClaimsIdentity)ticket.Principal.Identity;

                notification.Username = identity.Name;
                notification.Scope = ticket.GetProperty(OpenIdConnectConstants.Extra.Scope);

                // Potentially sensitive claims are only exposed to trusted callers
                // if the ticket corresponds to an access or identity token.
                if (ticket.IsAccessToken() || ticket.IsIdentityToken())
                {
                    foreach (var claim in ticket.Principal.Claims)
                    {
                        // Exclude standard claims, that are already handled via strongly-typed properties.
                        // Make sure to always update this list when adding new built-in claim properties.
                        if (string.Equals(claim.Type, identity.NameClaimType, StringComparison.Ordinal) ||
                            string.Equals(claim.Type, ClaimTypes.NameIdentifier, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (string.Equals(claim.Type, JwtRegisteredClaimNames.Aud, StringComparison.Ordinal) ||
                            string.Equals(claim.Type, JwtRegisteredClaimNames.Exp, StringComparison.Ordinal) ||
                            string.Equals(claim.Type, JwtRegisteredClaimNames.Iat, StringComparison.Ordinal) ||
                            string.Equals(claim.Type, JwtRegisteredClaimNames.Iss, StringComparison.Ordinal) ||
                            string.Equals(claim.Type, JwtRegisteredClaimNames.Nbf, StringComparison.Ordinal) ||
                            string.Equals(claim.Type, JwtRegisteredClaimNames.Sub, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (string.Equals(claim.Type, OpenIdConnectConstants.Claims.TokenType, StringComparison.Ordinal) ||
                            string.Equals(claim.Type, OpenIdConnectConstants.Claims.Scope, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        string type;
                        // Try to resolve the short name associated with the claim type:
                        // if none can be found, the claim type is used as-is.
                        if (!JwtSecurityTokenHandler.DefaultOutboundClaimTypeMap.TryGetValue(claim.Type, out type))
                        {
                            type = claim.Type;
                        }

                        // Note: make sure to use the indexer
                        // syntax to avoid duplicate properties.
                        notification.Claims[type] = claim.Value;
                    }
                }
            }

            await Options.Provider.ValidationEndpoint(notification);

            // Flow the changes made to the authentication ticket.
            ticket = notification.AuthenticationTicket;

            if (notification.HandledResponse)
            {
                return;
            }

            var payload = new JObject();

            payload.Add(OpenIdConnectConstants.Claims.Active, notification.Active);

            if (!string.IsNullOrEmpty(notification.Issuer))
            {
                payload.Add(JwtRegisteredClaimNames.Iss, notification.Issuer);
            }

            if (!string.IsNullOrEmpty(notification.Username))
            {
                payload.Add(OpenIdConnectConstants.Claims.Username, notification.Username);
            }

            if (!string.IsNullOrEmpty(notification.Subject))
            {
                payload.Add(JwtRegisteredClaimNames.Sub, notification.Subject);
            }

            if (!string.IsNullOrEmpty(notification.Scope))
            {
                payload.Add(OpenIdConnectConstants.Claims.Scope, notification.Scope);
            }

            if (notification.IssuedAt.HasValue)
            {
                payload.Add(JwtRegisteredClaimNames.Iat, EpochTime.GetIntDate(notification.IssuedAt.Value.UtcDateTime));
                payload.Add(JwtRegisteredClaimNames.Nbf, EpochTime.GetIntDate(notification.IssuedAt.Value.UtcDateTime));
            }

            if (notification.ExpiresAt.HasValue)
            {
                payload.Add(JwtRegisteredClaimNames.Exp, EpochTime.GetIntDate(notification.ExpiresAt.Value.UtcDateTime));
            }

            if (!string.IsNullOrEmpty(notification.TokenType))
            {
                payload.Add(OpenIdConnectConstants.Claims.TokenType, notification.TokenType);
            }

            switch (notification.Audiences.Count)
            {
                case 0: break;

                case 1:
                    payload.Add(JwtRegisteredClaimNames.Aud, notification.Audiences[0]);
                    break;

                default:
                    payload.Add(JwtRegisteredClaimNames.Aud, JArray.FromObject(notification.Audiences));
                    break;
            }

            foreach (var claim in notification.Claims)
            {
                // Ignore claims whose value is null.
                if (claim.Value == null)
                {
                    continue;
                }

                // Note: make sure to use the indexer
                // syntax to avoid duplicate properties.
                payload[claim.Key] = claim.Value;
            }

            var context = new ValidationEndpointResponseContext(Context, Options, payload);
            await Options.Provider.ValidationEndpointResponse(context);

            if (context.HandledResponse)
            {
                return;
            }

            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer)))
            {
                payload.WriteTo(writer);
                writer.Flush();

                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                Response.Headers[HeaderNames.CacheControl] = "no-cache";
                Response.Headers[HeaderNames.Pragma] = "no-cache";
                Response.Headers[HeaderNames.Expires] = "-1";

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Context.RequestAborted);
            }
        }

        private async Task<bool> InvokeLogoutEndpointAsync()
        {
            OpenIdConnectMessage request = null;

            // In principle, logout requests must be made via GET. Nevertheless,
            // POST requests are also allowed so that the inner application can display a logout form.
            // See https://openid.net/specs/openid-connect-session-1_0.html#RPLogout
            if (!string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                return await SendErrorPageAsync(new OpenIdConnectMessage
                {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed logout request has been received: " +
                        "make sure to use either GET or POST."
                });
            }

            if (string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                request = new OpenIdConnectMessage(Request.Query.ToDictionary())
                {
                    RequestType = OpenIdConnectRequestType.LogoutRequest
                };
            }

            else {
                // See http://openid.net/specs/openid-connect-core-1_0.html#FormSerialization
                if (string.IsNullOrEmpty(Request.ContentType))
                {
                    return await SendErrorPageAsync(new OpenIdConnectMessage
                    {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed logout request has been received: " +
                            "the mandatory 'Content-Type' header was missing from the POST request."
                    });
                }

                // May have media/type; charset=utf-8, allow partial match.
                if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                {
                    return await SendErrorPageAsync(new OpenIdConnectMessage
                    {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed logout request has been received: " +
                            "the 'Content-Type' header contained an unexcepted value. " +
                            "Make sure to use 'application/x-www-form-urlencoded'."
                    });
                }

                var form = await Request.ReadFormAsync(Context.RequestAborted);

                request = new OpenIdConnectMessage(form.ToDictionary())
                {
                    RequestType = OpenIdConnectRequestType.LogoutRequest
                };
            }

            // Store the logout request in the ASP.NET context.
            Context.SetOpenIdConnectRequest(request);

            // Note: post_logout_redirect_uri is not a mandatory parameter.
            // See http://openid.net/specs/openid-connect-session-1_0.html#RPLogout
            if (!string.IsNullOrEmpty(request.PostLogoutRedirectUri))
            {
                var clientNotification = new ValidateClientLogoutRedirectUriContext(Context, Options, request);
                await Options.Provider.ValidateClientLogoutRedirectUri(clientNotification);

                if (clientNotification.IsRejected)
                {
                    Logger.LogVerbose("Unable to validate client information");

                    return await SendErrorPageAsync(new OpenIdConnectMessage
                    {
                        Error = clientNotification.Error,
                        ErrorDescription = clientNotification.ErrorDescription,
                        ErrorUri = clientNotification.ErrorUri
                    });
                }
            }

            var notification = new LogoutEndpointContext(Context, Options, request);
            await Options.Provider.LogoutEndpoint(notification);

            if (notification.HandledResponse)
            {
                return true;
            }

            return false;
        }

        private async Task<string> SerializeAuthorizationCodeAsync(
     ClaimsPrincipal principal, AuthenticationProperties properties,
     OpenIdConnectMessage request, OpenIdConnectMessage response)
        {
            try
            {
                // properties.IssuedUtc and properties.ExpiresUtc
                // should always be preferred when explicitly set.
                if (properties.IssuedUtc == null)
                {
                    properties.IssuedUtc = Options.SystemClock.UtcNow;
                }

                if (properties.ExpiresUtc == null)
                {
                    properties.ExpiresUtc = properties.IssuedUtc + Options.AuthorizationCodeLifetime;
                }

                properties.SetUsage(OpenIdConnectConstants.Usages.Code);

                // Claims in authorization codes are never filtered as they are supposed to be opaque:
                // SerializeAccessTokenAsync and SerializeIdentityTokenAsync are responsible of ensuring
                // that subsequent access and identity tokens are correctly filtered.
                var ticket = new AuthenticationTicket(principal, properties, Options.AuthenticationScheme);

                var notification = new SerializeAuthorizationCodeContext(Context, Options, request, response, ticket)
                {
                    DataFormat = Options.AuthorizationCodeFormat
                };

                // Sets the default authorization code serializer.
                notification.Serializer = payload => {
                    return Task.FromResult(notification.DataFormat?.Protect(payload));
                };

                await Options.Provider.SerializeAuthorizationCode(notification);

                // Treat a non-null authorization code like an implicit HandleResponse call.
                if (notification.HandledResponse || !string.IsNullOrEmpty(notification.AuthorizationCode))
                {
                    return notification.AuthorizationCode;
                }

                else if (notification.Skipped)
                {
                    return null;
                }

                // Allow the application to change the authentication
                // ticket from the SerializeAuthorizationCode event.
                ticket = notification.AuthenticationTicket;
                ticket.Properties.CopyTo(properties);

                var key = GenerateKey(256 / 8);

                using (var stream = new MemoryStream())
                using (var writter = new StreamWriter(stream))
                {
                    writter.Write(await notification.SerializeTicketAsync());
                    writter.Flush();

                    await Options.Cache.SetAsync(key, options => {
                        options.AbsoluteExpiration = ticket.Properties.ExpiresUtc;

                        return stream.ToArray();
                    });
                }

                return key;
            }

            catch (Exception exception)
            {
                Logger.LogWarning("An exception occured when serializing an authorization code.", exception);

                return null;
            }
        }

        private async Task<string> SerializeAccessTokenAsync(
            ClaimsPrincipal principal, AuthenticationProperties properties,
            OpenIdConnectMessage request, OpenIdConnectMessage response)
        {
            try
            {
                // properties.IssuedUtc and properties.ExpiresUtc
                // should always be preferred when explicitly set.
                if (properties.IssuedUtc == null)
                {
                    properties.IssuedUtc = Options.SystemClock.UtcNow;
                }

                if (properties.ExpiresUtc == null)
                {
                    properties.ExpiresUtc = properties.IssuedUtc + Options.AccessTokenLifetime;
                }

                properties.SetUsage(OpenIdConnectConstants.Usages.AccessToken);

                // Create a new principal containing only the filtered claims.
                // Actors identities are also filtered (delegation scenarios).
                principal = principal.Clone(claim => {
                    // ClaimTypes.NameIdentifier and JwtRegisteredClaimNames.Sub are never excluded.
                    if (string.Equals(claim.Type, ClaimTypes.NameIdentifier, StringComparison.Ordinal) ||
                        string.Equals(claim.Type, JwtRegisteredClaimNames.Sub, StringComparison.Ordinal))
                    {
                        return true;
                    }

                    // Claims whose destination is not explicitly referenced or
                    // doesn't contain "token" are not included in the access token.
                    return claim.HasDestination(OpenIdConnectConstants.ResponseTypes.Token);
                });

                var identity = (ClaimsIdentity)principal.Identity;

                // Create a new ticket containing the updated properties and the filtered principal.
                var ticket = new AuthenticationTicket(principal, properties, Options.AuthenticationScheme);

                var notification = new SerializeAccessTokenContext(Context, Options, request, response, ticket)
                {
                    Client = request.ClientId,
                    Confidential = properties.IsConfidential(),
                    DataFormat = Options.AccessTokenFormat,
                    Issuer = Context.GetIssuer(Options),
                    SecurityTokenHandler = Options.AccessTokenHandler,
                    SigningCredentials = Options.SigningCredentials.FirstOrDefault()
                };

                foreach (var audience in properties.GetResources())
                {
                    notification.Audiences.Add(audience);
                }

                foreach (var scope in properties.GetScopes())
                {
                    notification.Scopes.Add(scope);
                }

                // Sets the default access token serializer.
                notification.Serializer = payload => {
                    if (notification.SecurityTokenHandler == null)
                    {
                        return Task.FromResult(notification.DataFormat?.Protect(payload));
                    }

                    // Extract the main identity from the principal.
                    identity = (ClaimsIdentity)payload.Principal.Identity;

                    // Store the "usage" property as a claim.
                    identity.AddClaim(OpenIdConnectConstants.Extra.Usage, payload.Properties.GetUsage());

                    // If the ticket is marked as confidential, add a new
                    // "confidential" claim in the security token.
                    if (notification.Confidential)
                    {
                        identity.AddClaim(new Claim(OpenIdConnectConstants.Extra.Confidential, "true", ClaimValueTypes.Boolean));
                    }

                    // Create a new claim per scope item, that will result
                    // in a "scope" array being added in the access token.
                    foreach (var scope in notification.Scopes)
                    {
                        identity.AddClaim(OpenIdConnectConstants.Claims.Scope, scope);
                    }

                    // Note: when used as an access token, a JWT token doesn't have to expose a "sub" claim
                    // but the name identifier claim is used as a substitute when it has been explicitly added.
                    // See https://tools.ietf.org/html/rfc7519#section-4.1.2
                    var subject = identity.FindFirst(JwtRegisteredClaimNames.Sub);
                    if (subject == null)
                    {
                        var identifier = identity.FindFirst(ClaimTypes.NameIdentifier);
                        if (identifier != null)
                        {
                            identity.AddClaim(JwtRegisteredClaimNames.Sub, identifier.Value);
                        }
                    }

                    // Remove the ClaimTypes.NameIdentifier claims to avoid getting duplicate claims.
                    // Note: the "sub" claim is automatically mapped by JwtSecurityTokenHandler
                    // to ClaimTypes.NameIdentifier when validating a JWT token.
                    // Note: make sure to call ToArray() to avoid an InvalidOperationException
                    // on old versions of Mono, where FindAll() is implemented using an iterator.
                    foreach (var claim in identity.FindAll(ClaimTypes.NameIdentifier).ToArray())
                    {
                        identity.RemoveClaim(claim);
                    }

                    // Store the audiences as claims.
                    foreach (var audience in notification.Audiences)
                    {
                        identity.AddClaim(JwtRegisteredClaimNames.Aud, audience);
                    }

                    // List the client application as an authorized party.
                    if (!string.IsNullOrEmpty(notification.Client))
                    {
                        identity.AddClaim(JwtRegisteredClaimNames.Azp, notification.Client);
                    }

                    var handler = notification.SecurityTokenHandler as JwtSecurityTokenHandler;
                    if (handler != null)
                    {
                        var token = handler.CreateToken(
                            subject: identity,
                            issuer: notification.Issuer,
                            signingCredentials: notification.SigningCredentials,
                            issuedAt: payload.Properties.IssuedUtc.Value.UtcDateTime,
                            notBefore: payload.Properties.IssuedUtc.Value.UtcDateTime,
                            expires: payload.Properties.ExpiresUtc.Value.UtcDateTime);

                        if (notification.SigningCredentials != null)
                        {
                            var x509SecurityKey = notification.SigningCredentials.Key as X509SecurityKey;
                            if (x509SecurityKey != null)
                            {
                                // Note: unlike "kid", "x5t" is not automatically added by JwtHeader's constructor in IdentityModel for ASP.NET 5.
                                // Though not required by the specifications, this property is needed for IdentityModel for Katana to work correctly.
                                // See https://github.com/aspnet-contrib/AspNet.Security.OpenIdConnect.Server/issues/132
                                // and https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet/issues/181.
                                token.Header[JwtHeaderParameterNames.X5t] = Base64UrlEncoder.Encode(x509SecurityKey.Certificate.GetCertHash());
                            }

                            object identifier;
                            if (!token.Header.TryGetValue(JwtHeaderParameterNames.Kid, out identifier) || identifier == null)
                            {
                                // When the token doesn't contain a "kid" parameter in the header, automatically
                                // add one using the identifier specified in the signing credentials.
                                identifier = notification.SigningCredentials.Kid;

                                if (identifier == null)
                                {
                                    // When no key identifier has been explicitly added by the developer, a "kid" is automatically
                                    // inferred from the hexadecimal representation of the certificate thumbprint (SHA-1).
                                    if (x509SecurityKey != null)
                                    {
                                        identifier = x509SecurityKey.Certificate.Thumbprint;
                                    }

                                    // When no key identifier has been explicitly added by the developer, a "kid"
                                    // is automatically inferred from the modulus if the signing key is a RSA key.
                                    var rsaSecurityKey = notification.SigningCredentials.Key as RsaSecurityKey;
                                    if (rsaSecurityKey != null)
                                    {
                                        // Only use the 40 first chars to match the identifier used by the JWKS endpoint.
                                        identifier = Base64UrlEncoder.Encode(rsaSecurityKey.Parameters.Modulus)
                                                                     .Substring(0, 40)
                                                                     .ToUpperInvariant();
                                    }
                                }

                                token.Header[JwtHeaderParameterNames.Kid] = identifier;
                            }
                        }

                        return Task.FromResult(handler.WriteToken(token));
                    }

                    else {
                        var token = notification.SecurityTokenHandler.CreateToken(new SecurityTokenDescriptor
                        {
                            Claims = payload.Principal.Claims,
                            Issuer = notification.Issuer,
                            Audience = notification.Audiences.ElementAtOrDefault(0),
                            SigningCredentials = notification.SigningCredentials,
                            IssuedAt = notification.AuthenticationTicket.Properties.IssuedUtc.Value.UtcDateTime,
                            NotBefore = notification.AuthenticationTicket.Properties.IssuedUtc.Value.UtcDateTime,
                            Expires = notification.AuthenticationTicket.Properties.ExpiresUtc.Value.UtcDateTime
                        });

                        // Note: the security token is manually serialized to prevent
                        // an exception from being thrown if the handler doesn't implement
                        // the SecurityTokenHandler.WriteToken overload returning a string.
                        var builder = new StringBuilder();
                        using (var writer = XmlWriter.Create(builder, new XmlWriterSettings
                        {
                            Encoding = new UTF8Encoding(false),
                            OmitXmlDeclaration = true
                        }))
                        {
                            notification.SecurityTokenHandler.WriteToken(writer, token);
                        }

                        return Task.FromResult(builder.ToString());
                    }
                };

                await Options.Provider.SerializeAccessToken(notification);

                // Treat a non-null access token like an implicit HandleResponse call.
                if (notification.HandledResponse || !string.IsNullOrEmpty(notification.AccessToken))
                {
                    return notification.AccessToken;
                }

                else if (notification.Skipped)
                {
                    return null;
                }

                // Allow the application to change the authentication
                // ticket from the SerializeAccessTokenAsync event.
                ticket = notification.AuthenticationTicket;
                ticket.Properties.CopyTo(properties);

                return await notification.SerializeTicketAsync();
            }

            catch (Exception exception)
            {
                Logger.LogWarning("An exception occured when serializing an access token.", exception);

                return null;
            }
        }

        private async Task<string> SerializeIdentityTokenAsync(
            ClaimsPrincipal principal, AuthenticationProperties properties,
            OpenIdConnectMessage request, OpenIdConnectMessage response)
        {
            try
            {
                // properties.IssuedUtc and properties.ExpiresUtc
                // should always be preferred when explicitly set.
                if (properties.IssuedUtc == null)
                {
                    properties.IssuedUtc = Options.SystemClock.UtcNow;
                }

                if (properties.ExpiresUtc == null)
                {
                    properties.ExpiresUtc = properties.IssuedUtc + Options.IdentityTokenLifetime;
                }

                properties.SetUsage(OpenIdConnectConstants.Usages.IdToken);

                // Replace the principal by a new one containing only the filtered claims.
                // Actors identities are also filtered (delegation scenarios).
                principal = principal.Clone(claim => {
                    // ClaimTypes.NameIdentifier and JwtRegisteredClaimNames.Sub are never excluded.
                    if (string.Equals(claim.Type, ClaimTypes.NameIdentifier, StringComparison.Ordinal) ||
                        string.Equals(claim.Type, JwtRegisteredClaimNames.Sub, StringComparison.Ordinal))
                    {
                        return true;
                    }

                    // Claims whose destination is not explicitly referenced or
                    // doesn't contain "id_token" are not included in the identity token.
                    return claim.HasDestination(OpenIdConnectConstants.ResponseTypes.IdToken);
                });

                var identity = (ClaimsIdentity)principal.Identity;

                // Create a new ticket containing the updated properties and the filtered principal.
                var ticket = new AuthenticationTicket(principal, properties, Options.AuthenticationScheme);

                var notification = new SerializeIdentityTokenContext(Context, Options, request, response, ticket)
                {
                    Confidential = properties.IsConfidential(),
                    Issuer = Context.GetIssuer(Options),
                    Nonce = request.Nonce,
                    SecurityTokenHandler = Options.IdentityTokenHandler,
                    SigningCredentials = Options.SigningCredentials.FirstOrDefault()
                };

                // If a nonce was present in the authorization request, it MUST
                // be included in the id_token generated by the token endpoint.
                // See http://openid.net/specs/openid-connect-core-1_0.html#IDTokenValidation
                if (request.IsAuthorizationCodeGrantType())
                {
                    // Restore the nonce stored in the authentication
                    // ticket extracted from the authorization code.
                    notification.Nonce = properties.GetNonce();
                }

                // While the 'sub' claim is declared mandatory by the OIDC specs,
                // it is not always issued as-is by the authorization servers.
                // When missing, the name identifier claim is used as a substitute.
                // See http://openid.net/specs/openid-connect-core-1_0.html#IDToken
                notification.Subject = identity.GetClaim(JwtRegisteredClaimNames.Sub) ??
                                       identity.GetClaim(ClaimTypes.NameIdentifier);

                // Only add client_id in the audiences list if it is non-null.
                if (!string.IsNullOrEmpty(request.ClientId))
                {
                    notification.Audiences.Add(request.ClientId);
                }

                if (!string.IsNullOrEmpty(response.Code))
                {
                    using (var algorithm = SHA256.Create())
                    {
                        // Create the c_hash using the authorization code returned by SerializeAuthorizationCodeAsync.
                        var hash = algorithm.ComputeHash(Encoding.ASCII.GetBytes(response.Code));

                        // Note: only the left-most half of the hash of the octets is used.
                        // See http://openid.net/specs/openid-connect-core-1_0.html#HybridIDToken
                        notification.CHash = Base64UrlEncoder.Encode(hash, 0, hash.Length / 2);
                    }
                }

                if (!string.IsNullOrEmpty(response.AccessToken))
                {
                    using (var algorithm = SHA256.Create())
                    {
                        // Create the at_hash using the access token returned by SerializeAccessTokenAsync.
                        var hash = algorithm.ComputeHash(Encoding.ASCII.GetBytes(response.AccessToken));

                        // Note: only the left-most half of the hash of the octets is used.
                        // See http://openid.net/specs/openid-connect-core-1_0.html#CodeIDToken
                        notification.AtHash = Base64UrlEncoder.Encode(hash, 0, hash.Length / 2);
                    }
                }

                // Sets the default identity token serializer.
                notification.Serializer = payload => {
                    if (notification.SecurityTokenHandler == null)
                    {
                        return Task.FromResult<string>(null);
                    }

                    if (string.IsNullOrEmpty(notification.Subject))
                    {
                        throw new InvalidOperationException(
                            "A unique identifier cannot be found to generate a 'sub' claim. " +
                            "Make sure to either add a 'sub' or a 'ClaimTypes.NameIdentifier' claim " +
                            "in the returned ClaimsIdentity before calling SignIn.");
                    }

                    // Extract the main identity from the principal.
                    identity = (ClaimsIdentity)payload.Principal.Identity;

                    // Store the unique subject identifier as a claim.
                    identity.AddClaim(JwtRegisteredClaimNames.Sub, notification.Subject);

                    // Remove the ClaimTypes.NameIdentifier claims to avoid getting duplicate claims.
                    // Note: the "sub" claim is automatically mapped by JwtSecurityTokenHandler
                    // to ClaimTypes.NameIdentifier when validating a JWT token.
                    // Note: make sure to call ToArray() to avoid an InvalidOperationException
                    // on old versions of Mono, where FindAll() is implemented using an iterator.
                    foreach (var claim in identity.FindAll(ClaimTypes.NameIdentifier).ToArray())
                    {
                        identity.RemoveClaim(claim);
                    }

                    // Store the "usage" property as a claim.
                    identity.AddClaim(OpenIdConnectConstants.Extra.Usage, payload.Properties.GetUsage());

                    // Store the audiences as claims.
                    foreach (var audience in notification.Audiences)
                    {
                        identity.AddClaim(JwtRegisteredClaimNames.Aud, audience);
                    }

                    if (!string.IsNullOrEmpty(notification.AtHash))
                    {
                        identity.AddClaim("at_hash", notification.AtHash);
                    }

                    if (!string.IsNullOrEmpty(notification.CHash))
                    {
                        identity.AddClaim(JwtRegisteredClaimNames.CHash, notification.CHash);
                    }

                    if (!string.IsNullOrEmpty(notification.Nonce))
                    {
                        identity.AddClaim(JwtRegisteredClaimNames.Nonce, notification.Nonce);
                    }

                    // If the ticket is marked as confidential, add a new
                    // "confidential" claim in the security token.
                    if (notification.Confidential)
                    {
                        identity.AddClaim(new Claim(OpenIdConnectConstants.Extra.Confidential, "true", ClaimValueTypes.Boolean));
                    }

                    var token = notification.SecurityTokenHandler.CreateToken(
                        subject: identity,
                        issuer: notification.Issuer,
                        signingCredentials: notification.SigningCredentials,
                        issuedAt: payload.Properties.IssuedUtc.Value.UtcDateTime,
                        notBefore: payload.Properties.IssuedUtc.Value.UtcDateTime,
                        expires: payload.Properties.ExpiresUtc.Value.UtcDateTime);

                    if (notification.SigningCredentials != null)
                    {
                        var x509SecurityKey = notification.SigningCredentials.Key as X509SecurityKey;
                        if (x509SecurityKey != null)
                        {
                            // Note: unlike "kid", "x5t" is not automatically added by JwtHeader's constructor in IdentityModel for ASP.NET 5.
                            // Though not required by the specifications, this property is needed for IdentityModel for Katana to work correctly.
                            // See https://github.com/aspnet-contrib/AspNet.Security.OpenIdConnect.Server/issues/132
                            // and https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet/issues/181.
                            token.Header[JwtHeaderParameterNames.X5t] = Base64UrlEncoder.Encode(x509SecurityKey.Certificate.GetCertHash());
                        }

                        object identifier;
                        if (!token.Header.TryGetValue(JwtHeaderParameterNames.Kid, out identifier) || identifier == null)
                        {
                            // When the token doesn't contain a "kid" parameter in the header, automatically
                            // add one using the identifier specified in the signing credentials.
                            identifier = notification.SigningCredentials.Kid;

                            if (identifier == null)
                            {
                                // When no key identifier has been explicitly added by the developer, a "kid" is automatically
                                // inferred from the hexadecimal representation of the certificate thumbprint (SHA-1).
                                if (x509SecurityKey != null)
                                {
                                    identifier = x509SecurityKey.Certificate.Thumbprint;
                                }

                                // When no key identifier has been explicitly added by the developer, a "kid"
                                // is automatically inferred from the modulus if the signing key is a RSA key.
                                var rsaSecurityKey = notification.SigningCredentials.Key as RsaSecurityKey;
                                if (rsaSecurityKey != null)
                                {
                                    // Only use the 40 first chars to match the identifier used by the JWKS endpoint.
                                    identifier = Base64UrlEncoder.Encode(rsaSecurityKey.Parameters.Modulus)
                                                                 .Substring(0, 40)
                                                                 .ToUpperInvariant();
                                }
                            }

                            token.Header[JwtHeaderParameterNames.Kid] = identifier;
                        }
                    }

                    return Task.FromResult(notification.SecurityTokenHandler.WriteToken(token));
                };

                await Options.Provider.SerializeIdentityToken(notification);

                // Treat a non-null identity token like an implicit HandleResponse call.
                if (notification.HandledResponse || !string.IsNullOrEmpty(notification.IdentityToken))
                {
                    return notification.IdentityToken;
                }

                else if (notification.Skipped)
                {
                    return null;
                }

                // Allow the application to change the authentication
                // ticket from the SerializeIdentityTokenAsync event.
                ticket = notification.AuthenticationTicket;
                ticket.Properties.CopyTo(properties);

                return await notification.SerializeTicketAsync();
            }

            catch (Exception exception)
            {
                Logger.LogWarning("An exception occured when serializing an identity token.", exception);

                return null;
            }
        }

        private async Task<string> SerializeRefreshTokenAsync(
            ClaimsPrincipal principal, AuthenticationProperties properties,
            OpenIdConnectMessage request, OpenIdConnectMessage response)
        {
            try
            {
                // properties.IssuedUtc and properties.ExpiresUtc
                // should always be preferred when explicitly set.
                if (properties.IssuedUtc == null)
                {
                    properties.IssuedUtc = Options.SystemClock.UtcNow;
                }

                if (properties.ExpiresUtc == null)
                {
                    properties.ExpiresUtc = properties.IssuedUtc + Options.RefreshTokenLifetime;
                }

                properties.SetUsage(OpenIdConnectConstants.Usages.RefreshToken);

                // Claims in refresh tokens are never filtered as they are supposed to be opaque:
                // SerializeAccessTokenAsync and SerializeIdentityTokenAsync are responsible of ensuring
                // that subsequent access and identity tokens are correctly filtered.
                var ticket = new AuthenticationTicket(principal, properties, Options.AuthenticationScheme);

                var notification = new SerializeRefreshTokenContext(Context, Options, request, response, ticket)
                {
                    DataFormat = Options.RefreshTokenFormat
                };

                // Sets the default refresh token serializer.
                notification.Serializer = payload => {
                    return Task.FromResult(notification.DataFormat?.Protect(payload));
                };

                await Options.Provider.SerializeRefreshToken(notification);

                // Treat a non-null refresh token like an implicit HandleResponse call.
                if (notification.HandledResponse || !string.IsNullOrEmpty(notification.RefreshToken))
                {
                    return notification.RefreshToken;
                }

                else if (notification.Skipped)
                {
                    return null;
                }

                // Allow the application to change the authentication
                // ticket from the SerializeRefreshTokenAsync event.
                ticket = notification.AuthenticationTicket;
                ticket.Properties.CopyTo(properties);

                return await notification.SerializeTicketAsync();
            }

            catch (Exception exception)
            {
                Logger.LogWarning("An exception occured when serializing a refresh token.", exception);

                return null;
            }
        }

        private async Task<AuthenticationTicket> DeserializeAuthorizationCodeAsync(string code, OpenIdConnectMessage request)
        {
            try
            {
                var notification = new DeserializeAuthorizationCodeContext(Context, Options, request, code)
                {
                    DataFormat = Options.AuthorizationCodeFormat
                };

                // Sets the default deserializer used to resolve the
                // authentication ticket corresponding to the authorization code.
                notification.Deserializer = payload => {
                    return Task.FromResult(notification.DataFormat?.Unprotect(payload));
                };

                await Options.Provider.DeserializeAuthorizationCode(notification);

                // Directly return the authentication ticket if one
                // has been provided by DeserializeAuthorizationCode.
                // Treat a non-null ticket like an implicit HandleResponse call.
                if (notification.HandledResponse || notification.AuthenticationTicket != null)
                {
                    if (notification.AuthenticationTicket == null)
                    {
                        return null;
                    }

                    // Ensure the received ticket is an authorization code.
                    if (!notification.AuthenticationTicket.IsAuthorizationCode())
                    {
                        Logger.LogVerbose("The received token was not an authorization code: {0}.", code);

                        return null;
                    }

                    return notification.AuthenticationTicket;
                }

                else if (notification.Skipped)
                {
                    return null;
                }

                var buffer = await Options.Cache.GetAsync(code);
                if (buffer == null)
                {
                    return null;
                }

                using (var stream = new MemoryStream(buffer))
                using (var reader = new StreamReader(stream))
                {
                    // Because authorization codes are guaranteed to be unique, make sure
                    // to remove the current code from the global store before using it.
                    await Options.Cache.RemoveAsync(code);

                    var ticket = await notification.DeserializeTicketAsync(await reader.ReadToEndAsync());
                    if (ticket == null)
                    {
                        return null;
                    }

                    // Ensure the received ticket is an authorization code.
                    if (!ticket.IsAuthorizationCode())
                    {
                        Logger.LogVerbose("The received token was not an authorization code: {0}.", code);

                        return null;
                    }

                    return ticket;
                }
            }

            catch (Exception exception)
            {
                Logger.LogWarning("An exception occured when deserializing an authorization code.", exception);

                return null;
            }
        }

        private async Task<AuthenticationTicket> DeserializeAccessTokenAsync(string token, OpenIdConnectMessage request)
        {
            try
            {
                var notification = new DeserializeAccessTokenContext(Context, Options, request, token)
                {
                    DataFormat = Options.AccessTokenFormat,
                    Issuer = Context.GetIssuer(Options),
                    SecurityTokenHandler = Options.AccessTokenHandler,
                    SignatureProvider = Options.SignatureProvider,
                    SigningKey = Options.SigningCredentials.Select(credentials => credentials.Key).FirstOrDefault()
                };

                // Sets the default deserializer used to resolve the
                // authentication ticket corresponding to the access token.
                notification.Deserializer = payload => {
                    var handler = notification.SecurityTokenHandler as ISecurityTokenValidator;
                    if (handler == null)
                    {
                        return Task.FromResult(notification.DataFormat?.Unprotect(payload));
                    }

                    // Create new validation parameters to validate the security token.
                    // ValidateAudience and ValidateLifetime are always set to false:
                    // if necessary, the audience and the expiration can be validated
                    // in InvokeValidationEndpointAsync or InvokeTokenEndpointAsync.
                    var parameters = new TokenValidationParameters
                    {
                        IssuerSigningKey = notification.SigningKey,
                        ValidIssuer = notification.Issuer,
                        ValidateAudience = false,
                        ValidateLifetime = false
                    };

                    SecurityToken securityToken;
                    var principal = handler.ValidateToken(payload, parameters, out securityToken);

                    // Parameters stored in AuthenticationProperties are lost
                    // when the identity token is serialized using a security token handler.
                    // To mitigate that, they are inferred from the claims or the security token.
                    var properties = new AuthenticationProperties
                    {
                        ExpiresUtc = securityToken.ValidTo,
                        IssuedUtc = securityToken.ValidFrom
                    };

                    var audiences = principal.FindAll(JwtRegisteredClaimNames.Aud);
                    if (audiences.Any())
                    {
                        properties.SetAudiences(audiences.Select(claim => claim.Value));
                    }

                    var usage = principal.FindFirst(OpenIdConnectConstants.Extra.Usage);
                    if (usage != null)
                    {
                        properties.SetUsage(usage.Value);
                    }

                    if (principal.Claims.Any(claim => claim.Type == OpenIdConnectConstants.Extra.Confidential))
                    {
                        properties.Items[OpenIdConnectConstants.Extra.Confidential] = "true";
                    }

                    return Task.FromResult(new AuthenticationTicket(principal, properties, Options.AuthenticationScheme));
                };

                await Options.Provider.DeserializeAccessToken(notification);

                // Directly return the authentication ticket if one
                // has been provided by DeserializeAccessToken.
                // Treat a non-null ticket like an implicit HandleResponse call.
                if (notification.HandledResponse || notification.AuthenticationTicket != null)
                {
                    if (notification.AuthenticationTicket == null)
                    {
                        return null;
                    }

                    // Ensure the received ticket is an access token.
                    if (!notification.AuthenticationTicket.IsAccessToken())
                    {
                        Logger.LogVerbose("The received token was not an access token: {0}.", token);

                        return null;
                    }

                    return notification.AuthenticationTicket;
                }

                else if (notification.Skipped)
                {
                    return null;
                }

                var ticket = await notification.DeserializeTicketAsync(token);
                if (ticket == null)
                {
                    return null;
                }

                // Ensure the received ticket is an access token.
                if (!ticket.IsAccessToken())
                {
                    Logger.LogVerbose("The received token was not an access token: {0}.", token);

                    return null;
                }

                return ticket;
            }

            catch (Exception exception)
            {
                Logger.LogWarning("An exception occured when deserializing an access token.", exception);

                return null;
            }
        }

        private async Task<AuthenticationTicket> DeserializeIdentityTokenAsync(string token, OpenIdConnectMessage request)
        {
            try
            {
                var notification = new DeserializeIdentityTokenContext(Context, Options, request, token)
                {
                    Issuer = Context.GetIssuer(Options),
                    SecurityTokenHandler = Options.IdentityTokenHandler,
                    SignatureProvider = Options.SignatureProvider,
                    SigningKey = Options.SigningCredentials.Select(credentials => credentials.Key).FirstOrDefault()
                };

                // Sets the default deserializer used to resolve the
                // authentication ticket corresponding to the identity token.
                notification.Deserializer = payload => {
                    if (notification.SecurityTokenHandler == null)
                    {
                        return Task.FromResult<AuthenticationTicket>(null);
                    }

                    // Create new validation parameters to validate the security token.
                    // ValidateAudience and ValidateLifetime are always set to false:
                    // if necessary, the audience and the expiration can be validated
                    // in InvokeValidationEndpointAsync or InvokeTokenEndpointAsync.
                    var parameters = new TokenValidationParameters
                    {
                        IssuerSigningKey = notification.SigningKey,
                        ValidIssuer = notification.Issuer,
                        ValidateAudience = false,
                        ValidateLifetime = false
                    };

                    SecurityToken securityToken;
                    var principal = notification.SecurityTokenHandler.ValidateToken(payload, parameters, out securityToken);

                    // Parameters stored in AuthenticationProperties are lost
                    // when the identity token is serialized using a security token handler.
                    // To mitigate that, they are inferred from the claims or the security token.
                    var properties = new AuthenticationProperties
                    {
                        ExpiresUtc = securityToken.ValidTo,
                        IssuedUtc = securityToken.ValidFrom
                    };

                    var audiences = principal.FindAll(JwtRegisteredClaimNames.Aud);
                    if (audiences.Any())
                    {
                        properties.SetAudiences(audiences.Select(claim => claim.Value));
                    }

                    var usage = principal.FindFirst(OpenIdConnectConstants.Extra.Usage);
                    if (usage != null)
                    {
                        properties.SetUsage(usage.Value);
                    }

                    if (principal.Claims.Any(claim => claim.Type == OpenIdConnectConstants.Extra.Confidential))
                    {
                        properties.Items[OpenIdConnectConstants.Extra.Confidential] = "true";
                    }

                    return Task.FromResult(new AuthenticationTicket(principal, properties, Options.AuthenticationScheme));
                };

                await Options.Provider.DeserializeIdentityToken(notification);

                // Directly return the authentication ticket if one
                // has been provided by DeserializeIdentityToken.
                // Treat a non-null ticket like an implicit HandleResponse call.
                if (notification.HandledResponse || notification.AuthenticationTicket != null)
                {
                    if (notification.AuthenticationTicket == null)
                    {
                        return null;
                    }

                    // Ensure the received ticket is an identity token.
                    if (!notification.AuthenticationTicket.IsIdentityToken())
                    {
                        Logger.LogVerbose("The received token was not an identity token: {0}.", token);

                        return null;
                    }

                    return notification.AuthenticationTicket;
                }

                else if (notification.Skipped)
                {
                    return null;
                }

                var ticket = await notification.DeserializeTicketAsync(token);
                if (ticket == null)
                {
                    return null;
                }

                // Ensure the received ticket is an identity token.
                if (!ticket.IsIdentityToken())
                {
                    Logger.LogVerbose("The received token was not an identity token: {0}.", token);

                    return null;
                }

                return ticket;
            }

            catch (Exception exception)
            {
                Logger.LogWarning("An exception occured when deserializing an identity token.", exception);

                return null;
            }
        }

        private async Task<AuthenticationTicket> DeserializeRefreshTokenAsync(string token, OpenIdConnectMessage request)
        {
            try
            {
                var notification = new DeserializeRefreshTokenContext(Context, Options, request, token)
                {
                    DataFormat = Options.RefreshTokenFormat
                };

                // Sets the default deserializer used to resolve the
                // authentication ticket corresponding to the refresh token.
                notification.Deserializer = payload => {
                    return Task.FromResult(notification.DataFormat?.Unprotect(payload));
                };

                await Options.Provider.DeserializeRefreshToken(notification);

                // Directly return the authentication ticket if one
                // has been provided by DeserializeRefreshToken.
                // Treat a non-null ticket like an implicit HandleResponse call.
                if (notification.HandledResponse || notification.AuthenticationTicket != null)
                {
                    if (notification.AuthenticationTicket == null)
                    {
                        return null;
                    }

                    // Ensure the received ticket is a refresh token.
                    if (!notification.AuthenticationTicket.IsRefreshToken())
                    {
                        Logger.LogVerbose("The received token was not a refresh token: {0}.", token);

                        return null;
                    }

                    return notification.AuthenticationTicket;
                }

                else if (notification.Skipped)
                {
                    return null;
                }

                var ticket = await notification.DeserializeTicketAsync(token);
                if (ticket == null)
                {
                    return null;
                }

                // Ensure the received ticket is a refresh token.
                if (!ticket.IsRefreshToken())
                {
                    Logger.LogVerbose("The received token was not a refresh token: {0}.", token);

                    return null;
                }

                return ticket;
            }

            catch (Exception exception)
            {
                Logger.LogWarning("An exception occured when deserializing a refresh token.", exception);

                return null;
            }
        }

    }
}