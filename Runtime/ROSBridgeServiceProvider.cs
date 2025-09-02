using System;
using UnityEngine;

namespace ROSBridgeLib
{
    /// <summary>
    /// Base class for all Parameters passed to ServiceProviders
    /// Inherit from this using members which match your ROS Service defintion
    /// in both TYPE and NAME.
    /// Can be used as-is for services which do not take parameters.
    /// </summary>
    [Serializable]
    public class ServiceArgs : ROSMessage
    {
    }

    /// <summary>
    /// Base class for all Response values passed from ServiceProviders back to the caller
    /// Inherit from this using members which match your ROS Service definition
    /// in both TYPE and NAME.
    /// Can be used as-is for services which do not generate responses.
    /// </summary>
    [Serializable]
    public class ServiceResponse : ROSMessage
    {
    }

    /// <summary>
    /// Base for all ServiceProviders.
    /// </summary>
    public abstract class ROSBridgeServiceProvider
    {
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

        public ROSBridgeServiceProvider(string serviceName)
        {
            _topic = serviceName;
        }

        public ROSBridgeServiceProvider(string serviceName, string serviceType)
        {
            _topic = serviceName;
            _type = serviceType;
        }

        public abstract ServiceArgs ParseRequest(string request);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TArgs">The ServiceArgs class you intend to use with this Service</typeparam>
    public class ROSBridgeServiceProvider<TArgs> : ROSBridgeServiceProvider where TArgs : ServiceArgs
    {
        public ROSBridgeServiceProvider(string serviceName) : base(serviceName)
        {
        }
        public ROSBridgeServiceProvider(string serviceName, string serviceType) : base(serviceName, serviceType)
        {
        }

        public override ServiceArgs ParseRequest(string request)
        {
            return JsonUtility.FromJson<TArgs>(request);
        }
    }
}
