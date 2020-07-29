﻿using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text;
using YiDian.EventBus.MQ.KeyAttribute;

namespace YiDian.EventBus.MQ
{

    internal class TopicEventBusMQ : EventBusBase<ITopicEventBus, TopicSubscriber>, ITopicEventBus
    {
        readonly string brokerName = "amq.topic";
        public TopicEventBusMQ(ILogger<ITopicEventBus> logger, IServiceProvider autofac, IRabbitMQPersistentConnection persistentConnection, IEventSeralize seralize, int cacheCount = 100)
            : base(logger, autofac, seralize, persistentConnection, cacheCount)
        {
        }
        public TopicEventBusMQ(string brokerName, ILogger<ITopicEventBus> logger, IServiceProvider autofac, IRabbitMQPersistentConnection persistentConnection, IEventSeralize seralize, int cacheCount = 100)
            : base(logger, autofac, seralize, persistentConnection, cacheCount)
        {
            this.brokerName = brokerName ?? throw new ArgumentNullException(nameof(brokerName), "broker name can not be null");
            persistentConnection.TryConnect();
            var channel = persistentConnection.CreateModel();
            channel.ExchangeDeclare(brokerName, "topic", true, false, null);
            channel.Dispose();
        }
        public override string BROKER_NAME => brokerName;

        private void DoWork(SendItem item)
        {
            var fix = GetPubKey(item.Event);
            if (!string.IsNullOrEmpty(item.Prefix))
            {
                fix = item.Prefix + "." + fix;
            }
            Publish(item.Event, fix, out _, out _, item.Enable);
        }
        public override bool Publish<T>(T @event, out ulong tag, bool enableTransaction = false)
        {
            var fix = GetPubKey(@event);
            return Publish(@event, fix, out _, out tag, enableTransaction);
        }
        public bool PublishPrefix<T>(T @event, string prefix, out ulong tag, bool enableTransaction = false) where T : IMQEvent
        {
            return Publish(@event, prefix, out _, out tag, enableTransaction);
        }
        string GetPubKey<T>(T @event) where T : IMQEvent
        {
            var type = @event.GetType();
            var props = TypeEventBusMetas.GetProperties(type);
            if (props == null || props.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            foreach (var p in props)
            {
                var value = p.Property.Invoke(@event);
                if (value.GetType().IsValueType) value = ((int)value).ToString();
                sb.Append(value.ToString());
                sb.Append('.');
            }
            var key = sb.ToString();
            return key;
        }
        string GetSubKey<T>() where T : IMQEvent
        {
            var type = typeof(T);
            var props = TypeEventBusMetas.GetProperties(type);
            if (props == null || props.Count == 0) return string.Empty;
            return "#.";
        }
        string GetSubKey<T>(Expression<Func<T, bool>> where) where T : IMQEvent
        {
            var type = typeof(T);
            var props = TypeEventBusMetas.GetProperties(type);
            if (props == null || props.Count == 0) return string.Empty;
            var body = where.Body as BinaryExpression;
            var dic = new Dictionary<string, string>();
            GetMembers(body, dic);
            var sb = new StringBuilder();
            props.ForEach(e =>
            {
                if (dic.ContainsKey(e.Name))
                {
                    sb.Append(dic[e.Name]);
                    sb.Append('.');
                }
                else sb.Append("*.");
            });
            return sb.ToString();
        }
        void GetMembers(BinaryExpression body, Dictionary<string, string> dic)
        {
            var right = body.Right as BinaryExpression;
            if (!(body.Left is BinaryExpression left))
            {
                if (body.Left is MemberExpression member)
                {
                    var name = member.Member.Name;
                    var value = body.Right.GetParameExpressionValue();
                    if (value != null)
                        dic.Add(name, value.ToString());
                }
                else if (body.Left is UnaryExpression unary)
                {
                    var name = (unary.Operand as MemberExpression).Member.Name;
                    var value = body.Right.GetParameExpressionValue();
                    if (value != null)
                        dic.Add(name, value.ToString());
                }
            }
            else
            {
                GetMembers(left, dic);
                if (right != null) GetMembers(right, dic);
            }
        }
        public void RegisterConsumer(string queueName, Action<TopicSubscriber> action, ushort fetchCount, int queueLength, bool autodel, bool durable, bool autoAck, bool autoStart)
        {
            if (string.IsNullOrEmpty(queueName)) return;
            var config = consumerInfos.Find(x => x.Name == queueName);
            if (config != null) return;
            var scriber = new TopicSubscriber(this, queueName);
            var submgr = GetSubscriber(queueName);
            config = new ConsumerConfig<ITopicEventBus, TopicSubscriber>(scriber, submgr)
            {
                AutoAck = autoAck,
                MaxLength = queueLength,
                Durable = durable,
                AutoDel = autodel,
                FetchCount = fetchCount,
                Name = queueName,
                SubAction = action
            };
            consumerInfos.Add(config);
            CreateConsumerChannel(config, autoStart);
        }

        public void Subscribe<T, TH>(string queueName, Expression<Func<T, bool>> where)
             where T : IMQEvent
             where TH : IEventHandler<T>
        {
            foreach (var item in consumerInfos)
            {
                if (item.Name == queueName)
                {
                    var mgr = item.GetSubMgr();
                    var eventKey = mgr.GetEventKey<T>();
                    var subkey = GetSubKey(where);
                    subkey += eventKey;
                    mgr.AddSubscription<T, TH>(subkey, BROKER_NAME);
                    break;
                }
            }
        }

        public void Unsubscribe<T>(string queueName, Expression<Func<T, bool>> where)
            where T : IMQEvent
        {
            foreach (var item in consumerInfos)
            {
                if (item.Name == queueName)
                {
                    var mgr = item.GetSubMgr();
                    var eventKey = mgr.GetEventKey<T>();
                    var subkey = GetSubKey(where);
                    subkey += eventKey;
                    mgr.RemoveSubscription(subkey, BROKER_NAME);
                    break;
                }
            }
        }

        public void Subscribe<T, TH>(string queueName, string subkey)
              where T : IMQEvent
             where TH : IEventHandler<T>
        {
            foreach (var item in consumerInfos)
            {
                if (item.Name == queueName)
                {
                    var mgr = item.GetSubMgr();
                    var eventKey = mgr.GetEventKey<T>();
                    subkey += "." + eventKey;
                    mgr.AddSubscription<T, TH>(subkey, BROKER_NAME);
                    break;
                }
            }
        }
        public void Unsubscribe<T>(string queueName, string subkey)
              where T : IMQEvent
        {
            foreach (var item in consumerInfos)
            {
                if (item.Name == queueName)
                {
                    var mgr = item.GetSubMgr();
                    var eventKey = mgr.GetEventKey<T>();
                    subkey += "." + eventKey;
                    mgr.RemoveSubscription(subkey, BROKER_NAME);
                    break;
                }
            }
        }
        public override void Subscribe<T, TH>(string queueName)
        {
            foreach (var item in consumerInfos)
            {
                if (item.Name == queueName)
                {
                    var mgr = item.GetSubMgr();
                    var eventKey = mgr.GetEventKey<T>();
                    var subkey = GetSubKey<T>() + eventKey;
                    mgr.AddSubscription<T, TH>(subkey, BROKER_NAME);
                    break;
                }
            }
        }

        public override void Unsubscribe<T, TH>(string queueName)
        {
            foreach (var item in consumerInfos)
            {
                if (item.Name == queueName)
                {
                    var mgr = item.GetSubMgr();
                    mgr.RemoveSubscription<T, TH>();
                    break;
                }
            }
        }
    }
    struct SendItem
    {
        public IMQEvent Event { get; }
        public bool Enable { get; }
        public string Prefix { get; }
        public SendItem(IMQEvent events, string prefix, bool enable)
        {
            Prefix = prefix;
            Event = events;
            Enable = enable;
        }
    }
}