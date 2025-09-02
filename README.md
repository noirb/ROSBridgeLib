# ROSBridgeLib
A [Rosbridge Suite](https://github.com/RobotWebTools/rosbridge_suite) Websocket client for Unity, providing a C# API modeled after the roscpp API.

Relies on the [Native WebSockets](https://github.com/endel/NativeWebSocket) library for the underlying [websocket implementation](./Runtime/WebSocket).

## Installation Instructions
1. In Unity, open the Package Manager (`Window -> Package Manager`)
2. In the Add Package menu, choose `Add package from git url...`
3. Enter `git@github.com:noirb/ROSBridgeLib.git` to get the latest version.
    * You can specify an explicit version by appending a tag or commit hash to the end of the URL:
      ```
      git@github.com:noirb/ROSBridgeLib.git#v1.0.0
      ```
    * If your SSH keys are protected with a passphrase, please refer to https://docs.unity3d.com/Manual/upm-errors.html#ssh-add


## Message Generation
For ROS 1 messages, use [gencs](https://github.com/noirb/gencs).

For ROS 2 messages, use [rosidl_generator_cs](https://github.com/noirb/rosidl_generator_cs).

After generating messages, place the generated C# files anywhere in your project's `Assets` directory (often it may be convenient to put `std_msgs` and the like in a separate location from your project-specific messages, but this is up to your personal preference). If you need to use both ROS1 and ROS2 in the same project, it is recommended to put all ROS1 messages in a separate namespace from ROS2 messages to avoid conflicts.

For convenience, several common message types are included in [messaging](./Runtime/messaging/) and [messaging_ros1](./Runtime/messaging_ros1/)

## API Usage

### Connecting to ROS
Connections to ROS are managed through the `ROSBridgeWebSocketConnection` class.

```C#
// create a new connection, giving it the ws://address and port to connect to
ROSBridgeWebSocketConnection connection = new ROSBridgeWebSocketConnection(address, port);

// these events can be useful to react to the state of the connection
    // called when connection cannot be made or is unexpectedly closed
connection.onConnectionFailure += () => Debug.Log("Failed to create websocket connection!");
    // called when the connection is opened and ready
connection.onConnectionSuccess += () => Debug.Log("Connection successful!");
    // called any time the state of the connection changes (including the above two cases)
connection.onConnectionStateChanged += (newState) => Debug.Log("New Connection state: " + newState.ToString());

// Set a maximum number of connection attempts to perform, initially or when a connection closes unexpectedly. Default: 1
connection.maxConnectionAttempts = 5;

// attempt to connect to the address and port specified above
connection.Connect();
```

This will connect to ROS and the connection can now begin receiving data. However, there is one more step which must be taken before received messages will be sent to your callbacks. In order to support the widest range of scenarios possible, you must find an appropriate location/thread to call `connection.Render()` from. Each time this method is called, it will attempt to process all accumulated messages and service calls that have arrived since the previous call.

Depending on your use case, you *probably* want to call this in either `Update()` or `FixedUpdate()` on a `GameObject`, but if your subscriber and service callbacks do not interact directly with any Unity objects you can also call this method from your own thread.

```C#
void FixedUpdate ()
{
    connection.Render()
}
```

#### Note:
> Before calling `Connect()`, all other operations (e.g. subscribing or publishing to topics) will be ignored, and after disconnecting (either by calling `Disconnect()` or from losing the connection for any other reason) all subscribers will automatically unsubscribe, publishers will automatically unadvertise their services, etc.

#### Note:
> To help ensure processing ROS callbacks doesn't tank rendering performance in Unity, a heuristic is applied to spend no more than half of the `fixedDeltaTime` on processing messages each time `Render()` is called. This is a tradeoff between processing all messages and allowing message processing to stall the application. Depending on the performance characteristics of your application, the work that needs to be done in your ROS callbacks, how many messages you need to process each frame, and whether you are calling `Render()` from the Unity render thread or your own thread, [you may want to adjust this heuristic here](./Runtime/ROSBridgeWebSocketConnection.cs#L653).

### Automatic reconnection
By default, when a connection is closed it will not be reopened until you call `Connect()` again. However, you may enable automatic reconnection attempts by setting `maxConnectionAttempts`:
```c#
// After 5 failed connection attempts, connection will give up and enter 'closed' state
connection.maxConnectionAttempts = 5;
// Use -1 to enable infinite retries. To stop automatic reconnections, you must then call Disconnect()
connection.maxConnectionAttempts = -1;
```

During reconnection attempts, `connection.state` will be `Connecting`. If `maxConnectionAttempts` is exhausted, `connection.state` will revert to `Closed`, and the `onConnectionFailure` and `onConnectionStateChanged` events will fire. Note that `onConnectionFailure` will also fire for every failed reconnection attempt.

If you set `maxConnectionAttempts` to `-1`, the connection may attempt to reconnect indefinitely. To stop the reconnection attempts in this case, call `Disconnect()`.

In the event of a disconnection followed by automatic reconnection, all subscribed/advertised topics and services will automatically be re-sent, so you should not need to re-subscribe/re-advertise after a reconnection.

### Publishing to a topic
To publish on a topic, you must first create a new publisher by calling `ROSBridgeWebSocketConnection.Advertise<Tmsg>(string topicName)`, where `Tmsg` is the message type you intend to publish. You can then call `ROSBridgeWebSocketConnection.Publish` with your publisher and the message you want to send:

```C#
ROSBridgePublisher my_pub = connection.Advertise<std_msgs.msg.Float32>("/my_topic");
connection.Publish(my_pub, new std_msgs.msg.Float32(42.2f));
```

### Subscribing to topics
Subscribing to a topic requires specifying the message type, topic name, and a callback function to be called with new messages. The callback may be a method or a lambda, and only needs to accept one parameter (matching the message type):

```C#
// with a lambda the message type can be inferred by the compiler and does not need be specified again
connection.Subscribe("/my_topic", (std_msgs.msg.Float32 msg) => { Debug.LogFormat("New Message: {0}", msg.data); });

// with a method callback the message type must be given
connection.Subscribe<std_msgs.msg.Float32>("/my_topic", myCallback);
...
public void myCallback(std_msgs.msg.Float32 msg)
{
    Debug.LogFormat("New message: {0}", msg.data);
}
```

#### NOTE:
> By default, all subscribers will be backed by an unlimited message queue. When subscribing to high frequency topics, or topics whose callbacks may be very expensive, it is recommended to specify a maximum queue size for your subscriber(s). If set, then any time a new message is received for a topic, if the queue is at its maximum size the oldest message in the queue will be discarded.

The maximum queue size can be set individually for each subscriber:
```C#
// queue size of 1: only the latest message will be processed each frame
connection.Subscribe<std_msgs.msg.Float32>("/float_topic", floatCallback, 1);
connection.Subscribe<geometry_msgs.msg.Pose>("/pose_topic", poseCallback, 10); // queue size of 10
```

Or, you can set a maximum queue size for ALL subscribers when creating the connection:
```C#
// Only accept the single latest message on all topics subscribed through this connection
ROSBridgeWebSocketConnection connection = new ROSBridgeWebSocketConnection(address, port, 1);
```

### Calling services
Because we cannot make a synchronous call which needs to wait for a network response from within Unity without breaking the render thread, only an asynchronous API is provided, which diverges slightly from the `roscpp` API:

```C#
CallService<TReq, TRes>(string serviceName, ServiceResponseCallback<TRes> callback, TReq serviceArgs = null)
```

`Treq` and `Tres` are the request and response message types. The callback must accept both `TRes` and a second `bool` parameter which contains the service call success/fail value. If this `bool` parameter is `false`, the response message may be `null` and should not be used (e.g. you may have attempted to call a service which does not exist).

```C#
connection.CallService("/set_bool", callbackFn, new std_srvs.srv.SetBool_Request(true));

private void callbackFn(std_srvs.srv.SetBool_Response resp, bool success)
{
    if (success)
    {
        Debug.LogFormat("Response {0}: {1}", resp.success, resp.message);
    }
    else
    {
        Debug.LogError("Service call fAILED");
    }
}
```

In "one line" with lambda:
```C#
connection.CallService("/set_bool",
    (std_srvs.srv.SetBool_Response res, bool success) =>
    {
        if (success)
            Debug.LogFormat("Response ({0}): {1}", res.success ? "succeeded" : "failed", res.message);
        else
            Debug.LogError("Service call FAILED");
    },
    new std_srvs.srv.SetBool_Request(true)
);
```

The `serviceArgs` parameter is optional and can be omitted if you are calling a service which takes no arguments. Note, however, that in this case the compiler will not be able to infer the type of `TReq`, and you will need to specify it at the call site.

```C#
connection.CallService<std_srvs.srv.Empty_Request, std_srvs.srv.Empty_Response>("/empty_srv",
    (std_srvs.srv.Empty_Response res, bool success) =>
    {
        if (success)
            Debug.LogFormat("Got empty Response!");
        else
            Debug.LogError("Empty Service call FAILED D:");
    }
);
```


### Providing services
To advertise a service, you must specify the service type, request type, and response type, as well as a callback method to handle incoming service calls. The message generators provided above will generate three clases for each service: `Service`, `Service_Request`, and `Service_Response`.

The callback method is responsible for two things:

1. If the service call expects a response, the response message must be created and initialized in the callback.
2. The callback retuns a `bool` value to indicate success/failure of the service call. **If you return `false`** this will indicate an **unrecoverable failure** and that the response data should not be read.

    From the caller's perspective, this kind of failure is indistinguishable from a service call which never even reached your code due to the message type being wrong, the service not existing, etc. In most cases, it's better to provide an error message in your response and to return `true` instead, as this will give the caller some information about what happened, allowing them to handle the failure more robustly.

```C#
using ROSBridgeLib.std_srvs.srv;
...
ROSBridgeServiceProvider srv = connection.Advertise<SetBool, SetBool_Request, SetBool_Response>("/setbool_srv", setbool_callback);
...
private bool setbool_callback(SetBool_Request args, out SetBool_Response resp)
{
    Debug.LogFormat("Got new bool: {0}", args.data);
    resp = new SetBool_Response();
    resp.success = true;
    resp.message = "Your bool was set!";

    return true; // false here indicates unrecoverable failure and potentially invalid data in resp
}
```
