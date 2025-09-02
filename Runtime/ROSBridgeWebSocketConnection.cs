using System.Collections.Generic;
using System.Threading;
using System.Reflection;
using System;
using System.Text.RegularExpressions;
using NativeWebSocket;
using UnityEngine;


namespace ROSBridgeLib
{
    public enum ConnectionState
    {
        Closed,
        Connecting,
        Connected
    }

    public delegate void OnConnectionStateChanged(ConnectionState newState);
    public delegate void OnConnectionOpenClosed();
    public delegate void ROSMessageCallback<in Tmsg>(Tmsg msg);
    public delegate bool ROSServiceCallback<in Targs, Tresp>(Targs args, out Tresp resp); // callback invoked for ROS->Unity service calls
    public delegate void ServiceResponseCallback<in Tresp>(Tresp resp, bool success); // used for callbacks to Unity->ROS service calls
    delegate void MessageCallback(ROSMessage msg); // internal; used to wrap ROSMessageCallbacks
    delegate bool ServiceCallback(ServiceArgs args, out ServiceResponse response); // internal; used to wrap ROSServiceCallbacks
    delegate void ServiceResponseCallback(ServiceResponse response, bool success); // internal; used to wrap ROSServiceResponseCallbacks

    [System.Serializable]
    public class Topper
    {
        public string op;
        public string topic;
        public string msg;

        public Topper(string jsonString)
        {

            Topper message = JsonUtility.FromJson<Topper>(jsonString);
            op = message.op;
            topic = message.topic;
            msg = message.msg;
        }
    }

    [System.Serializable]
    public class ServiceHeader
    {
        public string op;
        public string service;
        public string id;

        public ServiceHeader(string jsonstring)
        {
            JsonUtility.FromJsonOverwrite(jsonstring, this);
        }
    }

    [System.Serializable]
    public class ServiceResponseHeader
    {
        public string op;
        public string service;
        public string id;
        public string values;
        public bool result;

        public ServiceResponseHeader(string jsonstring)
        {
            JsonUtility.FromJsonOverwrite(jsonstring, this);
        }
    }

    public class ROSBridgeWebSocketConnection
    {
        /// <summary>
        /// A queue with a limited maximum size. If an object added to the queue causes the queue
        /// to go over the specified maximum size, it will automatically dequeue the oldest entry.
        /// A maximum size of 0 implies an unrestricted queue (making this equivalent to Queue<T>)
        /// </summary>
        /// <typeparam name="T"></typeparam>
        private class RenderQueue<T> : Queue<T>
        {
            // Maximum size of queue. Unrestricted if zero.
            private uint _maxSize = 0;

            public RenderQueue(uint maxSize)
            {
                _maxSize = maxSize;
            }

            public new void Enqueue(T obj)
            {
                base.Enqueue(obj);
                if (Count > _maxSize && _maxSize > 0)
                {
                    base.Dequeue();
                }
            }
        }

        private struct MessageTask
        {
            private ROSBridgeSubscriber _subscriber;
            private ROSMessage _msg;

            public MessageTask(ROSBridgeSubscriber subscriber, ROSMessage msg)
            {
                _subscriber = subscriber;
                _msg = msg;
            }

            public ROSBridgeSubscriber getSubscriber()
            {
                return _subscriber;
            }

            public ROSMessage getMsg()
            {
                return _msg;
            }
        };

        private struct PublishTask
        {
            private ROSBridgePublisher _publisher;
            private ROSMessage _msg;

            public PublishTask(ROSBridgePublisher publisher, ROSMessage msg)
            {
                _publisher = publisher;
                _msg = msg;
            }

            public ROSBridgePublisher publisher
            {
                get { return _publisher; }
            }

            public string message
            {
                get { return _publisher.ToMessage(_msg); }
            }
        }

        private class ServiceTask
        {
            private ROSBridgeServiceProvider _service;
            private ServiceArgs _request;
            private ServiceResponse _response;
            private string _id;

            public ServiceTask(ROSBridgeServiceProvider service, ServiceArgs request, string id)
            {
                _service = service;
                _request = request;
                _id = id;
            }

            public ROSBridgeServiceProvider Service
            {
                get { return _service; }
            }

            public ServiceArgs Request
            {
                get { return _request; }
            }

            public ServiceResponse Response
            {
                get { return _response; }
                set { _response = value; }
            }

            public string id
            {
                get { return _id; }
            }
        }

        private abstract class ServiceResponseHandlerTask
        {
            private string _id;
            private ServiceResponseCallback _cb;
            private ServiceResponse _response;
            private bool _success;

            public ServiceResponseHandlerTask(string id, ServiceResponseCallback callback)
            {
                _id = id;
                _cb = callback;
                _response = null;
                _success = false;
            }

            public void Invoke()
            {
                _cb(_response, _success);
            }

            public ServiceResponse Response
            {
                get { return _response; }
                set { _response = value; }
            }

            public string id
            {
                get { return _id; }
            }

            public bool succeeded
            {
                get { return _success; }
                set { _success = value; }
            }

            public abstract void ParseResponse(string jsonstring);
        }

        private class ServiceResponseHandlerTask<Tres> : ServiceResponseHandlerTask where Tres : ServiceResponse
        {
            public ServiceResponseHandlerTask(string id, ServiceResponseCallback callback) : base(id, callback)
            { }

            public override void ParseResponse(string jsonstring)
            {
                Response = JsonUtility.FromJson<Tres>(jsonstring);
            }
        }

        private ConnectionState _connectionState = ConnectionState.Closed;
        private string _host;
        private int _port;
        private int _connectionRetries;
        private WebSocket _ws;
        private Thread _recvThread;  // thread for reading data from the network
        private Dictionary<ROSBridgeSubscriber, MessageCallback> _subscribers; // our subscribers
        private List<ROSBridgePublisher> _publishers; // our publishers
        private Dictionary<ROSBridgeServiceProvider, ServiceCallback> _serviceServers; // service providers
        private Dictionary<string, ServiceResponseHandlerTask> _serviceResponseHandlers = new Dictionary<string, ServiceResponseHandlerTask>(); // handlers waiting for service responses, indexed by id
        private Dictionary<string, RenderQueue<MessageTask>> _msgQueue = new Dictionary<string, RenderQueue<MessageTask>>();
        private Dictionary<string, RenderQueue<ServiceTask>> _svcQueue = new Dictionary<string, RenderQueue<ServiceTask>>();
        private RenderQueue<ServiceResponseHandlerTask> _svcResQueue = new RenderQueue<ServiceResponseHandlerTask>(0);

        private Queue<PublishTask> _pubQueue = new Queue<PublishTask>();

        private object _readLock = new object();

        public OnConnectionStateChanged onConnectionStateChanged;
        public OnConnectionOpenClosed onConnectionSuccess;
        public OnConnectionOpenClosed onConnectionFailure;

        public bool connected
        {
            get { return _connectionState == ConnectionState.Connected; }
        }

        public ConnectionState state {
            get => _connectionState;
            private set
            {
                if (_connectionState != value)
                {
                    _connectionState = value;
                    onConnectionStateChanged?.Invoke(_connectionState);
                }
            }
        }

        public int maxConnectionAttempts { get; set; } = 1;

        public ROSBridgeWebSocketConnection(string host, int port) : this(host, port, 1) // default to msg queue size of 1 if unspecified
        {
        }

        public ROSBridgeWebSocketConnection(string host, int port, uint max_msg_queue_size)
        {
            _host = host;
            _port = port;
            _recvThread = null;
            _publishers = new List<ROSBridgePublisher>();
            _subscribers = new Dictionary<ROSBridgeSubscriber, MessageCallback>();
            _serviceServers = new Dictionary<ROSBridgeServiceProvider, ServiceCallback>();
        }

        /// <summary>
        /// Add a publisher to this connection. There can be many publishers.
        /// </summary>
        /// <typeparam name="Tpub">Publisher type to advertise</typeparam>
        /// <param name="topic">Topic to advertise on</param>
        /// <returns>A publisher which can be used to broadcast data on the given topic</returns>
        public ROSBridgePublisher<Tmsg> Advertise<Tmsg>(string topic) where Tmsg : ROSMessage
        {
            ROSBridgePublisher<Tmsg> pub = (ROSBridgePublisher<Tmsg>)Activator.CreateInstance(typeof(ROSBridgePublisher<Tmsg>), new object[] { topic });
            pub.SetBridgeConnection(this);

            _publishers.Add(pub);

            if (connected)
            {
                _ws.SendText(ROSBridgeMsg.AdvertiseTopic(pub.topic, pub.type));
            }

            return pub;
        }

        /// <summary>
        /// Remove a publisher from this connection
        /// </summary>
        /// <param name="pub"></param>
        public void Unadvertise(ROSBridgePublisher pub)
        {
            if (connected)
            {
                _ws.SendText(ROSBridgeMsg.UnAdvertiseTopic(pub.topic));
            }

            _publishers.Remove(pub);
        }

        /// <summary>
        /// Add a subscriber callback to this connection. There can be many subscribers.
        /// </summary>
        /// <typeparam name="Tmsg">Message type used in the callback</typeparam>
        /// <param name="sub">Subscriber</param>
        /// <param name="callback">Method to call when a message matching the given subscriber is received</param>
        public ROSBridgeSubscriber<Tmsg> Subscribe<Tmsg>(string topic, ROSMessageCallback<Tmsg> callback, uint queueSize = 0) where Tmsg : ROSMessage, new()
        {
            MessageCallback CB = (ROSMessage msg) =>
            {
                Tmsg message = msg as Tmsg;
                callback(message);
            };

            var getMessageType = typeof(Tmsg).GetMethod("GetMessageType");
            if (getMessageType == null)
            {
                Debug.LogError("[ROSBridgeLib] Could not retrieve method GetMessageType() from " + typeof(Tmsg).ToString());
                return null;
            }
            string messageType = (string)getMessageType.Invoke(null, null);
            if (messageType == null)
            {
                Debug.LogError("[ROSBridgeLib] Could not retrieve valid message type from " + typeof(Tmsg).ToString());
                return null;
            }

            ROSBridgeSubscriber<Tmsg> sub = new ROSBridgeSubscriber<Tmsg>(topic, messageType);

            lock (_readLock)
            {
                _subscribers.Add(sub, CB);
                if (!_msgQueue.ContainsKey(sub.topic))
                {
                    _msgQueue.Add(sub.topic, new RenderQueue<MessageTask>(queueSize));
                }
            }

            if (connected)
            {
                _ws.SendText(ROSBridgeMsg.Subscribe(sub.topic, sub.type));
            }

            return sub;
        }

        /// <summary>
        /// Remove a subscriber callback from this connection.
        /// </summary>
        /// <param name="sub"></param>
        public void Unsubscribe(ROSBridgeSubscriber sub)
        {
            if (sub == null)
            {
                return;
            }

            lock (_readLock)
            {
                _subscribers.Remove(sub);
            }

            // only remove incoming message queue and unsubscribe if this is the last subscriber for this topic
            bool keepQueue = false;
            foreach (var subscriber in _subscribers)
            {
                if (subscriber.Key.topic == sub.topic)
                {
                    keepQueue = true;
                    break;
                }
            }

            if (!keepQueue)
            {
                _msgQueue.Remove(sub.topic);

                if (connected)
                {
                    _ws.SendText(ROSBridgeMsg.UnSubscribe(sub.topic));
                }
            }
        }

        /// <summary>
        /// Add a Service server to this connection. There can be many servers, but each service should only have one.
        /// </summary>
        /// <typeparam name="Tsrv">ServiceProvider type</typeparam>
        /// <typeparam name="Treq">Message type containing parameters for this service</typeparam>
        /// <typeparam name="Tres">Message type containing response data returned by this service</typeparam>
        /// <param name="srv">The service to advertise</param>
        /// <param name="callback">Method to invoke when the service is called</param>
        public ROSBridgeServiceProvider<Treq> Advertise<Tsrv, Treq, Tres>(string service, ROSServiceCallback<Treq, Tres> callback) where Tsrv : ROSBridgeServiceProvider<Treq> where Treq : ServiceArgs where Tres : ServiceResponse, new()
        {
            ServiceCallback CB = (ServiceArgs args, out ServiceResponse response) =>
            {
                Treq request = (Treq)args;
                Tres res = new Tres();
                bool success = callback(request, out res);
                response = res;
                return success;
            };

            Tsrv srv = (Tsrv)Activator.CreateInstance(typeof(Tsrv), new object[] { service } );
            _serviceServers.Add(srv, CB);
            _svcQueue.Add(srv.topic, new RenderQueue<ServiceTask>(0));

            if (connected)
            {
                _ws.SendText(ROSBridgeMsg.AdvertiseService(srv.topic, srv.type));
            }

            return srv;
        }

        /// <summary>
        /// Remove a Service server from this connection
        /// </summary>
        /// <param name="srv"></param>
        public void Unadvertise(ROSBridgeServiceProvider srv)
        {
            if (connected)
            {
                _ws.SendText(ROSBridgeMsg.UnadvertiseService(srv.topic));
            }
            _serviceServers.Remove(srv);
        }

        /// <summary>
        /// Connect to the remote ros environment.
        /// </summary>
        public void Connect()
        {
            if (connected)
                return;

            _ws = new WebSocket(_host + ":" + _port);
            _ws.OnMessage += (bytes) => this.OnMessage(System.Text.Encoding.UTF8.GetString(bytes));
            _ws.OnError += _ws_OnError;
            _ws.OnOpen += _ws_OnOpen;
            _ws.OnClose += (msg) => _ws_OnError("Websocket connection closed. Reason: " + msg.ToString());
            state = ConnectionState.Connecting;

            _connectionRetries = maxConnectionAttempts;

            System.Threading.Tasks.Task.Run(async () =>
            {
                while (state == ConnectionState.Connecting && _connectionRetries != 0)
                {
                    _connectionRetries--;
                    await _ws.Connect();
                }
            });
        }

        /// <summary>
        /// Disconnect from the remote ros environment.
        /// </summary>
        public void Disconnect()
        {
            _connectionRetries = 0;
            
            if (state == ConnectionState.Closed)
                return;

            state = ConnectionState.Closed;

            if (connected)
            {
                foreach (var sub in _subscribers)
                {
                    _ws.SendText(ROSBridgeMsg.UnSubscribe(sub.Key.topic));
                }
                foreach (var p in _publishers)
                {
                    _ws.SendText(ROSBridgeMsg.UnAdvertiseTopic(p.topic));
                }
                foreach (var srv in _serviceServers)
                {
                    _ws.SendText(ROSBridgeMsg.UnadvertiseService(srv.Key.topic));
                }
            }

            if (_ws != null)
                _ws.Close();

            _msgQueue.Clear();
            _pubQueue.Clear();
            _svcQueue.Clear();
            _svcResQueue.Clear();

            if (_recvThread != null)
            {
                _recvThread.Abort();
                _recvThread.Join();
            }
        }

        private void Run()
        {
            lock (_readLock)
            {
                foreach (var sub in _subscribers)
                {
                    _ws.SendText(ROSBridgeMsg.Subscribe(sub.Key.topic, sub.Key.type)).Wait();
                    Debug.Log("[ROSBridgeLib] Sending: " + ROSBridgeMsg.Subscribe(sub.Key.topic, sub.Key.type));
                }
                foreach (var p in _publishers)
                {
                    _ws.SendText(ROSBridgeMsg.AdvertiseTopic(p.topic, p.type)).Wait();
                    Debug.Log("[ROSBridgeLib] Sending " + ROSBridgeMsg.AdvertiseTopic(p.topic, p.type));
                }
                foreach (var srv in _serviceServers)
                {
                    _ws.SendText(ROSBridgeMsg.AdvertiseService(srv.Key.topic, srv.Key.type)).Wait();
                    Debug.Log("[ROSBridgeLib] Sending: " + ROSBridgeMsg.AdvertiseService(srv.Key.topic, srv.Key.type));
                }
            }

            state = ConnectionState.Connected;
            if (onConnectionSuccess != null)
                onConnectionSuccess();

            while (connected)
            {
                Thread.Sleep(10);
#if !UNITY_WEBGL || UNITY_EDITOR
                _ws.DispatchMessageQueue();
#endif
            }

            Debug.Log("[ROSBridgeLib] WS Connection closed, exiting recv thread");
        }

        private void _ws_OnOpen()
        {
            Debug.Log("[ROSBridgeLib] Websocket open!");
            if (maxConnectionAttempts != 1)
                _connectionRetries = maxConnectionAttempts;

            _recvThread = new Thread(Run);
            _recvThread.Start();
        }

        private void _ws_OnError(string error)
        {
            Debug.LogErrorFormat("[ROSBridgeLib] {0}", error);
            if (_connectionRetries != 0)
            {
                state = ConnectionState.Connecting;
            }
            else if (state != ConnectionState.Closed)
            {
                state = ConnectionState.Closed;
                Disconnect();
            }

            if (onConnectionFailure != null)
                onConnectionFailure();
        }

        private void OnMessage(string s)
        {
            //Debug.Log("OnMessage: " + s);
            if ((s != null) && !s.Equals(""))
            {
                Topper mms = new Topper(s);

                string op = mms.op;

                if ("publish".Equals(op))
                {
                    string topic = mms.topic;
                    string msg_params = "";

                    // if we have message parameters, parse them
                    Match m = Regex.Match(s, @"""msg""\s*:\s*({.*})}"); //  "{"op": "publish", "topic": "/test/string", "msg": {"data": "jumpjump"}}"
                    ROSMessage msg = null;
                    if (m.Success)
                    {
                        msg_params = m.Groups[1].Value;
                    }

                    lock (_readLock)
                    {
                        foreach (var sub in _subscribers)
                        {
                            // only consider subscribers with a matching topic
                            if (topic != sub.Key.topic)
                                continue;

                            msg = sub.Key.ParseMessage(msg_params);
                            MessageTask newTask = new MessageTask(sub.Key, msg);

                            _msgQueue[topic].Enqueue(newTask);
                        }
                    }
                }
                else if (Regex.IsMatch(s, @"""op""\s*:\s*""call_service""")) // op is call_service
                {
                    ServiceHeader hdr = new ServiceHeader(s);
                    lock (_readLock)
                    {
                        foreach (var srv in _serviceServers)
                        {
                            if (srv.Key.topic == hdr.service)
                            {
                                ServiceArgs args = null;
                                // if we have args, parse them (args are optional on services, though)
                                Match m = Regex.Match(s, @"""args""\s*:\s*({.*}),");
                                if (m.Success)
                                {
                                    args = srv.Key.ParseRequest(m.Groups[1].Value);
                                }

                                // add service request to queue, to be processed later in Render()
                                _svcQueue[srv.Key.topic].Enqueue(new ServiceTask(srv.Key, args, hdr.id));

                                break; // there should only be one server for each service
                            }
                        }
                    }
                }
                else if (Regex.IsMatch(s, @"""op""\s*:\s*""service_response""")) // op is service_response
                {
                    ServiceResponseHeader hdr = new ServiceResponseHeader(s);

                    if (!_serviceResponseHandlers.ContainsKey(hdr.id))
                    {
                        Debug.LogErrorFormat("[ROSBridgeLib] Received a service response for '{0}', id: '{1}', but no callback exists!", hdr.service, hdr.id);
                    }
                    if (hdr.result)
                    {
                        Match m = Regex.Match(s, @"""values""\s*:\s*({.*}), ""result"""); // extract just the response values as json string
                        if (m.Success) // response values are optional
                        {
                            _serviceResponseHandlers[hdr.id].ParseResponse(m.Groups[1].Value);
                        }
                    }
                    _serviceResponseHandlers[hdr.id].succeeded = hdr.result;
                    _svcResQueue.Enqueue(_serviceResponseHandlers[hdr.id]);
                    _serviceResponseHandlers.Remove(hdr.id);
                }
                else
                {
                    Debug.LogWarning("[ROSBridgeLib] Unhandled message:\n" + s);
                }
            }
            else
            {
                Debug.Log("[ROSBridgeLib] Got an empty message from the web socket");
            }
        }

        /// <summary>
        /// Should be called at least once each frame. Calls any available callbacks for received messages.
        /// Note: MUST be called from Unity's main thread!
        /// </summary>
        public void Render()
        {
            float start = Time.realtimeSinceStartup;    // time at start of this frame
            float max_dur = 0.5f * Time.fixedDeltaTime; // max time we want to spend working
            float dur = 0.0f;                           // time spent so far processing messages

            while (dur < max_dur)
            {
                // get queued work to do
                List<MessageTask> msg_tasks = MessagePump();
                List<ServiceTask> svc_tasks = ServiceRequestPump();
                List<ServiceResponseHandlerTask> svcres_tasks = ServiceResponsePump();

                // bail if we have no work to do
                if (msg_tasks.Count == 0 && svc_tasks.Count == 0 && svcres_tasks.Count == 0)
                    break;

                // call all msg subsriber callbacks
                foreach (var t in msg_tasks)
                {
                    _subscribers[t.getSubscriber()](t.getMsg());
                }

                // call all svc handlers
                foreach (var svc in svc_tasks)
                {
                    ServiceResponse response = null;

                    // invoke service handler
                    bool success = _serviceServers[svc.Service](svc.Request, out response);

                    // send response
                    _ws.SendText(ROSBridgeMsg.ServiceResponse(success, svc.Service.topic, svc.id, JsonUtility.ToJson(response)));
                }

                // call service response handlers
                foreach (var handler in svcres_tasks)
                {
                    handler.Invoke();
                }

                dur = Time.realtimeSinceStartup - start;
            }
        }

        /// <summary>
        /// Pulls one message from each queue for processing
        /// </summary>
        /// <returns>A list of queued messages</returns>
        private List<MessageTask> MessagePump()
        {
            List<MessageTask> tasks = new List<MessageTask>();
            lock (_readLock)
            {
                foreach (var item in _msgQueue)
                {
                    // peel one entry from each queue to process on this frame
                    if (item.Value.Count > 0)
                    {
                        tasks.Add(item.Value.Dequeue());
                    }
                }
            }
            return tasks;
        }

        /// <summary>
        /// Pulls one service callback with results for processing
        /// </summary>
        /// <returns>A list of queued callbacks</returns>
        private List<ServiceResponseHandlerTask> ServiceResponsePump()
        {
            List<ServiceResponseHandlerTask> callbacks = new List<ServiceResponseHandlerTask>();
            lock(_readLock)
            {
                if (_svcResQueue.Count > 0)
                {
                    callbacks.Add(_svcResQueue.Dequeue());
                }
            }
            return callbacks;
        }

        /// <summary>
        /// Pulls one message from each service queue for processing
        /// </summary>
        /// <returns>A list of queued service requests</returns>
        private List<ServiceTask> ServiceRequestPump()
        {
            List<ServiceTask> tasks = new List<ServiceTask>();
            lock (_readLock)
            {
                foreach (var item in _svcQueue)
                {
                    // peel one entry from each queue to process on this frame
                    if (item.Value.Count > 0)
                    {
                        tasks.Add(item.Value.Dequeue());
                    }
                }
            }
            return tasks;
        }

        /// <summary>
        /// Calls a ROS service. The response will be sent to the given callback when render() is called.
        /// </summary>
        /// <typeparam name="TReq">ServiceRequest message type</typeparam>
        /// <typeparam name="TRes">ServiceResponse message type</typeparam>
        /// <param name="serviceName">Name of service to call</param>
        /// <param name="callback">Callback to handle service response.</param>
        /// <param name="serviceArgs">Arguments for service. Can be null for services without arguments.</param>
        long last_id = 1;
        public void CallService<TReq, TRes>(string serviceName, ServiceResponseCallback<TRes> callback, TReq serviceArgs = null) where TReq : ServiceArgs where TRes : ServiceResponse
        {
            ServiceResponseCallback CB = (msg, success) =>
            {
                if (!success)
                {
                    Debug.LogErrorFormat("[ROSBridgeLib] Service call FAILED: {0} (args: {1})", serviceName, serviceArgs != null ? JsonUtility.ToJson(serviceArgs) : "{}");
                }

                callback((TRes)msg, success);
            };

            if (_ws != null && connected)
            {
                string id = Interlocked.Increment(ref last_id).ToString();
                ServiceResponseHandlerTask task = new ServiceResponseHandlerTask<TRes>(id, CB);
                _serviceResponseHandlers.Add(id, task);
                _ws.SendText(ROSBridgeMsg.CallService(serviceName, id, JsonUtility.ToJson(serviceArgs)));
            }
            else
            {
                Debug.LogWarning("[ROSBridgeLib] Could not call service! No current connection to ROSBridge...");
            }
        }

        /// <summary>
        /// Publish a message to be sent to the ROS environment. Note: You must Advertise() before you can Publish().
        /// </summary>
        /// <param name="publisher">Publisher associated with the topic to publish to</param>
        /// <param name="msg">Message to publish</param>
        public void Publish(ROSBridgePublisher publisher, ROSMessage msg)
        {
            if (_ws != null && connected)
            {
                _ws.SendText(publisher.ToMessage(msg));
            }
            else
            {
                Debug.LogWarning("[ROSBridgeLib] Could not publish message! No current connection to ROSBridge...");
            }
        }

    }
}
