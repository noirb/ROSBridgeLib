using UnityEngine;


namespace ROSBridgeLib {
	public abstract class ROSBridgeSubscriber {
        protected string _topic;
        protected string _type;

        public string topic
        {
            get { return _topic; }
        }

        public string type
        {
            get { return _type; }
        }

        public ROSBridgeSubscriber(string topicName)
        {
            _topic = topicName;
        }

        public ROSBridgeSubscriber(string topicName, string typeName)
        {
            _topic = topicName;
            _type = typeName;
        }

        public abstract ROSMessage ParseMessage(string message);
    }

    public class ROSBridgeSubscriber<Tmsg> : ROSBridgeSubscriber where Tmsg : ROSMessage
    {
        public ROSBridgeSubscriber(string topicName) : base(topicName)
        {
            
        }

        public ROSBridgeSubscriber(string topicName, string typeName) : base(topicName, typeName)
        { }

        public override ROSMessage ParseMessage(string message)
        {
            return JsonUtility.FromJson<Tmsg>(message);
        }
    }
}

