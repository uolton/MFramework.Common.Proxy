/*   Copyright 2009 - 2010 Marcus Bratton

     Licensed under the Apache License, Version 2.0 (the "License");
     you may not use this file except in compliance with the License.
     You may obtain a copy of the License at

     http://www.apache.org/licenses/LICENSE-2.0

     Unless required by applicable law or agreed to in writing, software
     distributed under the License is distributed on an "AS IS" BASIS,
     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
     See the License for the specific language governing permissions and
     limitations under the License.
*/

using System;
using System.Collections;
using System.Reflection;
using MFramework.Common.Proxy.Attributes;
using MFramework.Common.Proxy.Interceptors.Methods.ProcessEncapsulating;
using Siege.TypeGenerator;

namespace MFramework.Common.Proxy
{
    public class SiegeProxy
{
    	private static readonly Hashtable definedTypes = new Hashtable();
        private bool useServiceLocator;
        private static readonly Siege.TypeGenerator.TypeGenerator generator = new Siege.TypeGenerator.TypeGenerator();

    	public SiegeProxy WithServiceLocator()
        {
            useServiceLocator = true;
            return this;
        }

        public Type Create<TProxy>() where TProxy : class
        {
            return Create(typeof (TProxy));
        }

        public Type Create(Type typeToProxy)
        {
            if (definedTypes.ContainsKey(typeToProxy)) return (Type)definedTypes[typeToProxy];
            if (!HasAopDefinitions(typeToProxy)) return typeToProxy;

            Type generatedType = generator.CreateType(type =>
            {
                type.Named(typeToProxy.Name + Guid.NewGuid());
                type.InheritFrom(typeToProxy);
                GeneratedField field = null;
                
                if(useServiceLocator)
                {
                    field = type.AddField<Microsoft.Practices.ServiceLocation.IServiceLocator>("serviceLocator");
                    type.AddConstructor(constructor => constructor.CreateArgument<Microsoft.Practices.ServiceLocation.IServiceLocator>().AssignTo(field));
                }

                ProxyMethods(type, typeToProxy, field);
            });

            return generatedType;
        }

        private static bool HasAopDefinitions(Type typeToProxy)
        {
            var methods = typeToProxy.GetMethods();

            for (int i = 0; i < methods.Length; i++)
            {
                var method = methods[i];
                if(method.GetCustomAttributes(typeof(IAopAttribute), true).Length > 1) return true;
            }

            return false;
        }

    	private void ProxyMethods(BaseTypeGenerationContext type, Type typeToProxy, GeneratedField field)
    	{
    	    var methods = typeToProxy.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < methods.Length; i++)
            {
                var methodInfo = methods[i];
                if (methodInfo.IsVirtual && methodInfo.GetBaseDefinition().DeclaringType != typeof(object))
                {
                    type.OverrideMethod(methodInfo, method => method.WithBody(body =>
                    {
                        if (methodInfo.GetCustomAttributes(typeof(IAopAttribute), true).Length == 0)
                        {
                            body.CallBase(methodInfo);
                            return;
                        }

                        GeneratedVariable locator = null;

                        if (useServiceLocator)
                        {
                            locator = body.CreateVariable<Microsoft.Practices.ServiceLocation.IServiceLocator>();
                            locator.AssignFrom(field);
                        }

                        GeneratePreProcessors(body, methodInfo, locator);

                        var returnValue = GenerateEncapsulatedCalls(methodInfo, body, locator, field);

                        GeneratePostProcessors(body, methodInfo, locator);

                        if (returnValue != null) body.Return(returnValue);
                    }));
                }
            }
    	}

    	private void GeneratePreProcessors(MethodBodyContext body, ICustomAttributeProvider methodInfo, GeneratedVariable serviceLocator)
        {
    	    var attributes = methodInfo.GetCustomAttributes(typeof (IPreProcessingAttribute), true);
            
            for(int i = 0; i < attributes.Length; i++)
            {
                var attribute = attributes[i];
                var preProcessor = body.CreateVariable<IPreProcessingAttribute>();
                if(useServiceLocator)
                {
                    preProcessor.AssignFrom(() => serviceLocator.Invoke(typeof(Microsoft.Practices.ServiceLocation.IServiceLocator).GetMethod("GetInstance", new Type[0]).MakeGenericMethod(attribute.GetType())));
                }
                else
                {
                    preProcessor.AssignFrom(body.Instantiate(attribute.GetType()));
				}

                preProcessor.Invoke<IDefaultPreProcessingAttribute>(processor => processor.Process());
            }
        }

        private void GeneratePostProcessors(MethodBodyContext body, ICustomAttributeProvider methodInfo, GeneratedVariable serviceLocator)
        {
            var attributes = methodInfo.GetCustomAttributes(typeof(IPostProcessingAttribute), true);

            for (int i = 0; i < attributes.Length; i++)
            {
                var attribute = attributes[i];
                var postProcessor = body.CreateVariable<IPostProcessingAttribute>();
                if (useServiceLocator)
                {
                    postProcessor.AssignFrom(() => serviceLocator.Invoke(typeof(Microsoft.Practices.ServiceLocation.IServiceLocator).GetMethod("GetInstance", new Type[0]).MakeGenericMethod(attribute.GetType())));
                }
                else
                {
                    postProcessor.AssignFrom(body.Instantiate(attribute.GetType()));
                }

                postProcessor.Invoke<IDefaultPostProcessingAttribute>(processor => processor.Process());
			}
        }

        private GeneratedVariable GenerateEncapsulatedCalls(MethodInfo methodInfo, MethodBodyContext body, GeneratedVariable serviceLocator, GeneratedField field)
        {
            var attributes = methodInfo.GetCustomAttributes(typeof(IProcessEncapsulatingAttribute), true);

            GeneratedVariable variable = null;

            if (attributes.Length == 0)
            {
                if(methodInfo.ReturnType == typeof(void))
                {
                    body.CallBase(methodInfo);
                }
                else
                {
                    variable = body.CreateVariable(methodInfo.ReturnType);
                    variable.AssignFrom(() => body.CallBase(methodInfo));
                }

                return variable;
            }

			var encapsulating = body.CreateVariable<IProcessEncapsulatingAttribute>();
            if (useServiceLocator)
			{
				encapsulating.AssignFrom(() => serviceLocator.Invoke(typeof (Microsoft.Practices.ServiceLocation.IServiceLocator).GetMethod("GetInstance", new Type[0]).MakeGenericMethod(attributes[0].GetType())));
			}
			else
			{
				encapsulating.AssignFrom(body.Instantiate(attributes[0].GetType()));
			}
        	MethodInfo target = null;
        	var lambdaVariable = body.CreateLambda(lambda =>
   			{
				target = lambda.Target(methodInfo);
				
   				RecursivelyGenerateCalls(attributes, 1, lambda, methodInfo, field);
   			});

			var func = lambdaVariable.CreateFunc(target);

			if (methodInfo.ReturnType != typeof(void))
			{
				variable = body.CreateVariable(methodInfo.ReturnType);
                new DefaultProcessEncapsulatingInterceptionStrategy().Intercept(methodInfo, attributes[0], func, variable, encapsulating);
			}
			else
			{
                new DefaultProcessEncapsulatingActionInterceptionStrategy().Intercept(methodInfo, attributes[0], func, null, encapsulating);
			}

        	return variable;
        }

    	private void RecursivelyGenerateCalls(object[] attributes, int currentIndex, DelegateBodyContext lambda, MethodInfo methodInfo, GeneratedField field)
    	{
			if (currentIndex >= attributes.Length) return;
    		var attribute = attributes[currentIndex];
			
			lambda.CreateNestedLambda(nestedLambda => RecursivelyGenerateCalls(attributes, currentIndex + 1, lambda, methodInfo, field),
			(exitContext, exitVariable, function, callingType) =>
			{
				var encapsulating = exitContext.CreateVariable<IProcessEncapsulatingAttribute>();

				if (useServiceLocator)
				{
				    var locator = exitContext.CreateVariable<Microsoft.Practices.ServiceLocation.IServiceLocator>();
				    locator.AssignFrom(field, callingType);
				    encapsulating.AssignFrom(() => locator.Invoke(typeof(Microsoft.Practices.ServiceLocation.IServiceLocator).GetMethod("GetInstance", new Type[0]).MakeGenericMethod(attribute.GetType())));
				}
				else
				{
				    encapsulating.AssignFrom(exitContext.Instantiate(attribute.GetType()));
				}

				var func = exitContext.CreateFunc(methodInfo.ReturnType, function());

				if (methodInfo.ReturnType != typeof(void))
				{
                    new DefaultProcessEncapsulatingInterceptionStrategy().Intercept(methodInfo, attribute, func, exitVariable, encapsulating);
				}
				else
				{
                    new DefaultProcessEncapsulatingActionInterceptionStrategy().Intercept(methodInfo, attribute, func, null, encapsulating);
				}
			});
		}

        public void SaveAssembly()
        {
            generator.Save();
        }
    }
}
