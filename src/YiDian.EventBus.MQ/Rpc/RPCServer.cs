﻿using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using Autofac;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using YiDian.Soa.Sp;
using YiDian.EventBus.MQ.Rpc.Route;
using YiDian.EventBus.MQ.Rpc.Abstractions;
using System.Collections.Generic;
using System.Text;

namespace YiDian.EventBus.MQ.Rpc
{
    internal class RPCServer : IRPCServer
    {
        const string BROKER_NAME = "rpc_event_bus";
        const string AUTOFAC_NAME = "rpc_event_bus";
        readonly ILogger<RPCServer> _logger;
        readonly ILifetimeScope _autofac;
        readonly IRabbitMQPersistentConnection _conn;
        readonly IQpsCounter _qps;
        readonly RoutingTables _routing;
        IModel _consumerChannel;
        IModel _pubChannel;
        internal RPCServer(IRabbitMQPersistentConnection conn, ILogger<RPCServer> logger, RpcServerConfig config, ILifetimeScope autofac, IQpsCounter qps)
        {
            config.ApplicationId = config.ApplicationId.ToLower();
            _routing = new RoutingTables();
            _routing.LoadControlers(config.ApplicationId);
            _autofac = autofac;
            _logger = logger;
            _conn = conn;
            _qps = qps;
            Configs = config;
            CreatePublishChannel();
            CreateConsumerChannel();
        }
        void CreatePublishChannel()
        {
            if (_pubChannel == null || _pubChannel.IsClosed)
            {
                if (!_conn.IsConnected)
                {
                    _conn.TryConnect();
                }
                _pubChannel = _conn.CreateModel();
                _pubChannel.CallbackException += (sender, ea) =>
                {
                    _pubChannel.Dispose();
                    _pubChannel = null;
                    CreatePublishChannel();
                };
            }
        }
        public RpcServerConfig Configs { get; }

        public string ServerId => Configs.ApplicationId;

        private void CreateConsumerChannel()
        {
            if (_consumerChannel != null && !_consumerChannel.IsClosed) return;
            if (!_conn.IsConnected) _conn.TryConnect();
            var channel = _conn.CreateModel();
            channel.ExchangeDeclare(BROKER_NAME, "direct", true, false);
            channel.BasicQos(0, Configs.Fetchout, false);
            channel.QueueDeclare(queue: Configs.ApplicationId, durable: false, exclusive: false, autoDelete: false, arguments: null);
            channel.QueueBind(Configs.ApplicationId, BROKER_NAME, Configs.ApplicationId, null);
            _consumerChannel = channel;
            StartConsumer();
        }
        private void StartConsumer()
        {
            var consumer = new EventingBasicConsumer(_consumerChannel);
            consumer.Received += (model, ea) =>
            {
                ProcessEvent(ea);
            };
            _consumerChannel.BasicConsume(queue: Configs.ApplicationId, autoAck: true, consumer: consumer);
        }


        private void ProcessEvent(BasicDeliverEventArgs ea)
        {
            _qps.Add("r");
            var header = new HeadersAnalysis(ea.Body);
            var clienttime = header.ClientDate;
            var span = Math.Abs((DateTime.Now - clienttime).TotalMilliseconds);
            if (Configs.Delay != 0 && span > Configs.Delay * 1000)
            {
                ReplayTo(header.Url, DateTime.Now, ea, 402, $"请求已超时 请求 {ea.RoutingKey} 耗时 {span.ToString()}ms", ContentType.Text);
                return;
            }
            var action = _routing.Route(header.Url.AbsolutePath);
            if (action == null)
            {
                ReplayTo(header.Url, DateTime.Now, ea, 401, "未找到匹配请求的控制器或方法", ContentType.Text);
                return;
            }
            var reques = new Request()
            {
                Headers = header.Headers,
                Action = action,
                Url = header.Url,
                Seralize = RPCWrite.CreateSeralize(header.ContentType, header.Encode),
                ContentLength = header.ContentLength,
                Body = header.GetBodys()
            };
            Excute(reques, ea);
        }

        private void ReplayTo(Uri url, DateTime intime, BasicDeliverEventArgs ea, int state, string msg, ContentType type, object obj = null, Type objType = null)
        {
            try
            {
                var replyTo = ea.BasicProperties.ReplyTo;
                var replyTold = ea.BasicProperties.CorrelationId;
                var replyProps = _pubChannel.CreateBasicProperties();
                replyProps.CorrelationId = replyTold;
                var s_url = url.ToString();
                _logger.LogInformation($"response uri:{s_url}, data:{obj.ToJson()} state={state.ToString()} type={type.ToString()}");
                if (type != ContentType.Json || (type == ContentType.Json && obj != null))
                {
                    var msg_size = Encoding.UTF8.GetByteCount(msg);
                    var datas = new byte[55 + msg_size + GetObjSize(type, obj, objType)];
                    var write = new RPCWrite(datas, Encoding.UTF8);
                    write.WriteString("encoding:utf-8");
                    write.WriteString("state:" + state.ToString());
                    write.WriteString("msg:" + msg);
                    write.WriteString("content-type:" + (type == ContentType.YDData ? "yddata" : (type == ContentType.Json ? "json" : "text")));
                    write.WriteContent(type, obj, objType);
                    _pubChannel.BasicPublish("", routingKey: replyTo, basicProperties: replyProps, body: write.GetDatas().ToArray());
                    var now = DateTime.Now;
                    var ms = (now - intime).TotalMilliseconds;
                    _qps.Set("c", (int)ms);
                }
                else throw new NotImplementedException();
            }
            catch (Exception ex)
            {
                _logger.LogError("Rpc ReplayTo Error:" + ex.ToString());
            }
        }
        public uint GetObjSize(ContentType type, object data, Type dataType)
        {
            if (data == null) return 0;
            var ser = RPCWrite.CreateSeralize(type, Encoding.UTF8);
            return ser.GetSize(data, dataType);
        }
        readonly static DateTime start = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public DateTime UnixTimestampToDate(long timestamp)
        {
            return start.AddSeconds(timestamp).ToLocalTime();
        }
        private async void Excute(Request req, BasicDeliverEventArgs ea)
        {
            req.InTime = DateTime.Now;
            var dic = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(req.Url.Query))
            {
                var args = req.Url.Query.Substring(1).Split('&');
                foreach (var item in args)
                {
                    var arr = item.Split('=');
                    dic.Add(arr[0], arr[1]);
                }
            }
            object[] m_args = new object[req.Action.InAags.Length];
            if (req.Action.InAags != null && req.Action.InAags.Length > 0)
            {
                for (var i = 0; i < req.Action.InAags.Length; i++)
                {
                    var p = req.Action.InAags[i];
                    var list = p.GetCustomAttributes(typeof(FromBodyAttribute), false);
                    if (list.Length <= 0)
                    {
                        if (dic.TryGetValue(p.Name, out string v)) m_args[i] = CreateParam(v, p.ParameterType);
                    }
                    else m_args[i] = ReadFromBody(req, p.ParameterType);
                }
            }
            var controller = GetController(req.Action, out ILifetimeScope scope);
            controller.Request = req;
            try
            {
                var obj = req.Action.Method(controller, m_args);
                if (obj == null) obj = new ActionResult<object>(null);
                object res;
                if (req.Action.IsTask)
                {
                    var task = obj as Task;
                    await task;
                    res = ((ActionResult)req.Action.GetTaskResult(obj)).GetResult();
                }
                else res = ((ActionResult)obj).GetResult();
                var type = (req.Action.ActionResultType.IsValueType || req.Action.ActionResultType == typeof(string)) ? ContentType.Text : ContentType.YDData;
                ReplayTo(req.Url, req.InTime, ea, 200, "", type, res, req.Action.ActionResultType);
            }
            catch (Exception ex)
            {
                ReplayTo(req.Url, req.InTime, ea, 500, ex.Message, ContentType.Text);
                _logger.LogError(req.Url.ToString() + " 执行异常：" + ex.ToString());
            }
            scope?.Dispose();
        }

        private object CreateParam(string v, Type t)
        {
            if (t == typeof(int)) return int.Parse(v);
            else if (t == typeof(uint)) return uint.Parse(v);
            else if (t == typeof(long)) return long.Parse(v);
            else if (t == typeof(ulong)) return ulong.Parse(v);
            else if (t == typeof(short)) return short.Parse(v);
            else if (t == typeof(ushort)) return ushort.Parse(v);
            else if (t == typeof(byte)) return byte.Parse(v);
            else if (t == typeof(double)) return double.Parse(v);
            else if (t == typeof(float)) return float.Parse(v);
            else if (t.IsEnum) return Enum.Parse(t, v);
            else return v;
        }

        private object ReadFromBody(Request req, Type type)
        {
            var obj = req.Seralize.DeserializeObject(req.Body, type);
            return obj;
        }

        private RpcController GetController(ActionInfo action, out ILifetimeScope scope)
        {
            scope = _autofac.BeginLifetimeScope(AUTOFAC_NAME);
            return scope.ResolveOptional(action.ControllerType) as RpcController;
        }
    }
}
