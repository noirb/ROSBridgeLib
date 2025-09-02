using System;
using System.Collections;

/**
 * This defines a publisher. There had better be a corresponding subscriber somewhere. This is really
 * just a holder for the message topic and message type.
 * 
 * Version History
 * 3.1 - changed methods to start with an upper case letter to be more consistent with c#
 * style.
 * 3.0 - modification from hand crafted version 2.0
 * 
 * @author Michael Jenkin, Robert Codd-Downey and Andrew Speers
 * @version 3.1
 */

namespace ROSBridgeLib {
    public abstract class ROSBridgePublisher {

        protected string _topic;
        protected string _type;
        protected ROSBridgeLib.ROSBridgeWebSocketConnection _ros;

        public string topic
        {
            get { return _topic; }
        }

        public string type
        {
            get { return _type; }
        }

        public ROSBridgePublisher(string topicName)
        {
            _topic = topicName;
        }

        public void SetBridgeConnection(ROSBridgeWebSocketConnection ros)
        {
            _ros = ros;
        }

        public abstract string ToMessage(ROSMessage Message);

        /// <summary>
        /// Used to wrap ROSMessages for transmission over the network
        /// </summary>
        /// <typeparam name="T">Message type</typeparam>
        [System.Serializable]
        protected class ROSMessageWrapper<T>
        {
            public string op;
            public string topic;
            public T msg;

            public ROSMessageWrapper(string _op, string _topic, T _data)
            {
                op = _op;
                topic = _topic;
                msg = _data;
            }
        }
    }

    public class ROSBridgePublisher<Tmsg> : ROSBridgePublisher where Tmsg : ROSMessage
    {
        public ROSBridgePublisher(string topicName) : base(topicName)
        {
            var getMessageType = typeof(Tmsg).GetMethod("GetMessageType");
            if (getMessageType == null)
            {
                UnityEngine.Debug.LogError("Could not retrieve method GetMessageType() from " + typeof(Tmsg).ToString());
                return;
            }
            string messageType = (string)getMessageType.Invoke(null, null);
            if (messageType == null)
            {
                UnityEngine.Debug.LogError("Could not retrieve valid message type from " + typeof(Tmsg).ToString());
                return;
            }

            _type = messageType;
        }

        public void Publish(Tmsg Message)
        {
            _ros.Publish(this, Message);
        }

        public override string ToMessage(ROSMessage Message)
        {
            var msg = new ROSMessageWrapper<Tmsg>("publish", _topic, (Tmsg)Message);
            return UnityEngine.JsonUtility.ToJson(msg);
        }

    }
}
