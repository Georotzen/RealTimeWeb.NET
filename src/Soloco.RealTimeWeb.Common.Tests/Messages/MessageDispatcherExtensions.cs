﻿using System;
using Soloco.RealTimeWeb.Common.Messages;

namespace Soloco.RealTimeWeb.Common.Tests.Messages
{
    public static class MessageDispatcherExtensions
    {
        public static T ExecuteNowWithTimeout<T>(this IMessageDispatcher messageDispatcher, IMessage<T> message)
        {
            if (messageDispatcher == null) throw new ArgumentNullException(nameof(messageDispatcher));
            if (message == null) throw new ArgumentNullException(nameof(message));

            var task = messageDispatcher.Execute(message);
            if (!task.Wait(60000))
            {
                throw new InvalidOperationException("Timeout on executing message.");
            }
            return task.Result;
        }
    }
}
