using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FubuCore.Conversion;

namespace FubuCore.Binding
{
    public class BindingContext : IBindingContext
    {
        private static readonly List<Func<string, string>> _namingStrategies;
        private readonly IServiceLocator _locator;
        private readonly IBindingLogger _logger;
        private readonly Stack<object> _objectStack = new Stack<object>();
        private readonly IList<ConvertProblem> _problems = new List<ConvertProblem>();
        private readonly IRequestData _requestData;
        private readonly Lazy<IObjectResolver> _resolver;
        private readonly Lazy<IContextValues> _values;


        static BindingContext()
        {
            _namingStrategies = new List<Func<string, string>>{
                p => p,
                p => p.Replace("_", "-"),
                p => "[{0}]".ToFormat(p) // This was necessary 
            };
        }

        public BindingContext(IRequestData requestData, IServiceLocator locator, IBindingLogger logger)
        {
            if (logger == null) throw new ArgumentNullException("logger");

            _requestData = requestData;
            _locator = locator;
            _logger = logger;
            _resolver = new Lazy<IObjectResolver>(() =>
            {
                if (_locator == null) return ObjectResolver.Basic();

                return _locator.GetInstance<IObjectResolver>();
            });

            _values = new Lazy<IContextValues>(() =>
            {
                var converter = _locator == null ? new ObjectConverter() : _locator.GetInstance<IObjectConverter>();
                var request = new SmartRequest(_requestData, converter);
                return new ContextValues(request, _namingStrategies, _requestData);
            });
        }

        /// <summary>
        ///   The underlying data for this binding context
        /// </summary>
        public IRequestData RequestData
        {
            get { return _requestData; }
        }

        public IBindingLogger Logger
        {
            get { return _logger; }
        }


        public IList<ConvertProblem> Problems
        {
            get { return _problems; }
        }

        public T Service<T>()
        {
            return _locator.GetInstance<T>();
        }

        public IContextValues Data
        {
            get { return _values.Value; }
        }

        public void ForProperty(PropertyInfo property, Action<IPropertyContext> action)
        {
            try
            {
                var propertyContext = new PropertyContext(this, _locator, property);
                action(propertyContext);
            }
            catch (Exception ex)
            {
                BindingValue value = null;
                RequestData.Value(property.Name, o => value = o);
                LogProblem(ex, value, property);
            }
        }

        public void ForObject(object @object, Action action)
        {
            StartObject(@object);
            action();
            FinishObject();
        }


        public object Object
        {
            get { return _objectStack.Any() ? _objectStack.Peek() : null; }
        }

        public void BindObject(IRequestData data, Type type, Action<object> continuation)
        {
            _resolver.Value.TryBindModel(type, data, result =>
            {
                // TODO -- log the value
                _problems.AddRange(result.Problems);

                continuation(result.Value);
            });
        }

        public static void AddNamingStrategy(Func<string, string> strategy)
        {
            _namingStrategies.Add(strategy);
        }

        public object Service(Type typeToFind)
        {
            return _locator.GetInstance(typeToFind);
        }

        public void StartObject(object @object)
        {
            _objectStack.Push(@object);
        }

        public void FinishObject()
        {
            _objectStack.Pop();
        }


        [MarkedForTermination("Still think this can die")]
        private BindingContext prefixWith(string prefix)
        {
            var prefixedData = _requestData.GetSubRequest(prefix);
            var child = new BindingContext(prefixedData, _locator, Logger);

            return child;
        }

        public void LogProblem(Exception ex, BindingValue value = null, PropertyInfo property = null)
        {
            LogProblem(ex.ToString(), value, property);
        }

        public void LogProblem(string exceptionText, BindingValue value = null, PropertyInfo property = null)
        {
            var problem = new ConvertProblem{
                ExceptionText = exceptionText,
                Item = Object,
                Property = property,
                Value = value
            };

            _problems.Add(problem);
        }

        public object BindObject(string prefixOrChild, Type childType)
        {
            var context = prefixWith(prefixOrChild);

            //need to determine if the item is in there or not since we will be greedy
            //and try to get as many items for our list as possible
            if (_requestData.HasAnyValuePrefixedWith(prefixOrChild))
            {
                var bindResult = _resolver.Value.BindModel(childType, context);
                return bindResult.Value;
            }
            return null;
        }
    }
}