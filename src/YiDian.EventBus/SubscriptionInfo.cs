﻿using System;

namespace YiDian.EventBus
{
    public class SubscriptionInfo
    {
        public bool IsDynamic { get; }
        public Type HandlerType { get; }
        public Type EventType { get; }
        public FastInvokeHandler Handler { get; }
        public string SubKey { get; }
        public string EventKey { get; }
        public bool CanRemoveSubByEvent { get; }

        private SubscriptionInfo(string subkey, string eventKey, bool isDynamic, bool canRemoveSubByEvent, Type handlerType, Type eventType, FastInvokeHandler handler)
        {
            SubKey = subkey;
            EventKey = eventKey;
            IsDynamic = isDynamic;
            HandlerType = handlerType;
            Handler = handler;
            EventType = eventType;
            CanRemoveSubByEvent = canRemoveSubByEvent;
        }
        public static SubscriptionInfo Dynamic(string subkey, string eventKey, bool canRemoveSubByEvent, Type handlerType, FastInvokeHandler handler)
        {
            return new SubscriptionInfo(subkey, eventKey, true, canRemoveSubByEvent, handlerType, null, handler);
        }
        public static SubscriptionInfo Typed(string subkey, string eventKey, bool canRemoveSubByEvent, Type handlerType, Type eventType, FastInvokeHandler handler)
        {
            return new SubscriptionInfo(subkey, eventKey, false, canRemoveSubByEvent, handlerType, eventType, handler);
        }
    }
}
