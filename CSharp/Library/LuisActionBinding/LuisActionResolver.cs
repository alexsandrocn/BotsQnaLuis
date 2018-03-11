﻿// 
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license.
// 
// Microsoft Bot Framework: http://botframework.com
// 
// Cognitive Services based Dialogs for Bot Builder:
// https://github.com/Microsoft/BotBuilder-CognitiveServices 
// 
// Copyright (c) Microsoft Corporation
// All rights reserved.
// 
// MIT License:
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

namespace Microsoft.Bot.Builder.CognitiveServices.LuisActionBinding
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Bot.Builder.Dialogs.Internals;
    using Microsoft.Bot.Builder.Luis;
    using Microsoft.Bot.Builder.Luis.Models;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// Handles retrieving an Action Binding based on the matched intent from the input utterance, validates parameter based on extracted entities.
    /// </summary>
    [Serializable]
    public class LuisActionResolver
    {
        private readonly IDictionary<string, Type> luisActions;

        /// <summary>
        /// Construct LuisActionResolver instance.
        /// </summary>
        /// <param name="lookupAssemblies">List of assemblies where to find Action Bindings Types.</param>
        public LuisActionResolver(params Assembly[] lookupAssemblies)
        {
            this.luisActions = new Dictionary<string, Type>();

            if (lookupAssemblies == null)
            {
                throw new ArgumentNullException(nameof(lookupAssemblies));
            }

            foreach (var lookupAssembly in lookupAssemblies)
            {
                foreach (var info in lookupAssembly.GetTypes().Select(t => new { Type = t, Attribs = t.GetCustomAttributes<LuisActionBindingAttribute>(true) }).Where(o => o.Attribs.Any()))
                {
                    foreach (var intentAttrib in info.Attribs)
                    {
                        this.luisActions.Add(intentAttrib.IntentName, info.Type);
                    }
                }
            }
        }

        /// <summary>
        /// Tries to assign a value to the action's parameter.
        /// This method runs the proper validation according to the action's parameter definition.
        /// </summary>
        /// <param name="action">The current action and context.</param>
        /// <param name="paramName">The parameter to try to assign.</param>
        /// <param name="paramValue">The parameter value to assign.</param>
        /// <returns>True if validation passes and the value was assigned. False if validation or type compatibility fails.</returns>
        public static bool AssignValue(ILuisAction action, string paramName, object paramValue)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (string.IsNullOrWhiteSpace(paramName))
            {
                throw new ArgumentNullException(nameof(paramName));
            }

            if (paramValue == null)
            {
                throw new ArgumentNullException(nameof(paramValue));
            }

            return AssignValue(action, action.GetType().GetProperty(paramName, BindingFlags.Public | BindingFlags.Instance), paramValue);
        }

        /// <summary>
        /// Tries to extract intent and entities from the input utterance and validates if the the current parameter should be updated or if a context switch is needed.
        /// </summary>
        /// <param name="service">Instance of Microsoft.Bot.Builder.Luis.ILuisService to use for extracting intent and entities.</param>
        /// <param name="action">The current action and context.</param>
        /// <param name="paramName">The parameter to try to assign.</param>
        /// <param name="paramValue">The parameter value to assign.</param>
        /// <param name="token">Cancellation Token.</param>
        /// <param name="entityExtractor">Optional: EntityExtractor to disambiguate multiple values into a single one.</param>
        /// <returns>Indicates if the Action Parameter was validated (and set) or a if a Context Switch should occur.</returns>
        public static async Task<QueryValueResult> QueryValueFromLuisAsync(
            ILuisService service,
            ILuisAction action,
            string paramName,
            object paramValue,
            CancellationToken token,
            Func<PropertyInfo, IEnumerable<EntityRecommendation>, EntityRecommendation> entityExtractor = null)
        {
            var originalValue = paramValue;

            if (service == null)
            {
                throw new ArgumentNullException(nameof(service));
            }

            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (string.IsNullOrWhiteSpace(paramName))
            {
                throw new ArgumentNullException(nameof(paramName));
            }

            if (paramValue == null)
            {
                throw new ArgumentNullException(nameof(paramValue));
            }

            var result = await service.QueryAsync(paramValue.ToString(), token);
            var queryIntent = result.Intents.FirstOrDefault();
            if (queryIntent != null && !Intents.None.Equals(queryIntent.Intent, StringComparison.InvariantCultureIgnoreCase))
            {
                var newIntentName = default(string);
                var newAction = new LuisActionResolver(action.GetType().Assembly).ResolveActionFromLuisIntent(result, out newIntentName);
                if (newAction != null && !newAction.GetType().Equals(action.GetType()))
                {
                    return new QueryValueResult(false)
                    {
                        NewAction = newAction,
                        NewIntent = newIntentName
                    };
                }
            }

            var properties = new List<PropertyInfo> { action.GetType().GetProperty(paramName, BindingFlags.Public | BindingFlags.Instance) };
            if (!LuisActionResolver.AssignEntitiesToMembers(action, properties, result.Entities, entityExtractor))
            {
                return new QueryValueResult(AssignValue(action, properties.First(), originalValue));
            }

            return new QueryValueResult(true);
        }

        /// <summary>
        /// Gets the Action definition from an action instance.
        /// </summary>
        /// <param name="action">The action instance.</param>
        /// <returns>The action definition.</returns>
        public static LuisActionBindingAttribute GetActionDefinition(ILuisAction action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            return action.GetType().GetCustomAttributes<LuisActionBindingAttribute>(true).FirstOrDefault();
        }

        /// <summary>
        /// Gets the Action Parameter definition from an action instance and parameter name.
        /// </summary>
        /// <param name="action">The action instance.</param>
        /// <param name="paramName">The current parameter's name.</param>
        /// <returns>The action parameter definition.</returns>
        public static LuisActionBindingParamAttribute GetParameterDefinition(ILuisAction action, string paramName)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (string.IsNullOrWhiteSpace(paramName))
            {
                throw new ArgumentNullException(nameof(paramName));
            }

            var prop = action.GetType().GetProperty(paramName, BindingFlags.Public | BindingFlags.Instance);

            // search binding attrib
            return prop.GetCustomAttributes<LuisActionBindingParamAttribute>(true).FirstOrDefault();
        }

        /// <summary>
        /// If the action is contextual and related to the current context action, set the contextual action to the current context.
        /// </summary>
        /// <param name="action">The contextual action to validate.</param>
        /// <param name="context">The current action context.</param>
        /// <param name="isContextual">Indicates if the contextual action is related to the current action.</param>
        public static bool IsValidContextualAction(ILuisAction action, ILuisAction context, out bool isContextual)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            isContextual = LuisActionResolver.IsContextualAction(action);

            if (!isContextual)
            {
                return false;
            }

            var validContextualType = typeof(ILuisContextualAction<>).MakeGenericType(context.GetType());
            if (validContextualType.IsAssignableFrom(action.GetType()))
            {
                var prop = validContextualType.GetProperty("Context", BindingFlags.Public | BindingFlags.Instance);
                prop.SetValue(action, context);

                return true;
            }

            return false;
        }

        /// <summary>
        /// If the action is contextual and related to the current context action, set the contextual action to the current context.
        /// </summary>
        /// <param name="action">The contextual action to validate.</param>
        /// <param name="context">The current action context.</param>
        /// <param name="isContextual">Indicates if the contextual action is related to the current action.</param>
        public static bool UpdateIfValidContextualAction(ILuisAction action, ILuisAction context, out bool isContextual)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            isContextual = false;

            if (!LuisActionResolver.IsContextualAction(context))
            {
                return true;
            }

            var prop = context.GetType().GetProperty("Context", BindingFlags.Public | BindingFlags.Instance);
            var actionContext = prop.GetValue(context) as ILuisAction;

            return LuisActionResolver.IsValidContextualAction(action, actionContext, out isContextual);
        }

        /// <summary>
        /// Indicates if the action is contextual to another action.
        /// </summary>
        /// <param name="action">The contextual action to validate.</param>
        public static bool IsContextualAction(ILuisAction action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            return action
                .GetType()
                .GetInterfaces()
                .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ILuisContextualAction<>));
        }

        /// <summary>
        /// Indicates if the action can be started, either is not a contextual action or it can create the contextual action directly.
        /// </summary>
        /// <param name="action">The action to validate.</param>
        /// <param name="actionDefinition">The action definition.</param>
        public static bool CanStartWithNoContextAction(ILuisAction action, out LuisActionBindingAttribute actionDefinition)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (!LuisActionResolver.IsContextualAction(action))
            {
                actionDefinition = null;
                return true;
            }

            actionDefinition = LuisActionResolver.GetActionDefinition(action);

            return actionDefinition.CanExecuteWithNoContext;
        }

        /// <summary>
        /// Builds the action from a contextual action.
        /// </summary>
        /// <param name="action">The contextual action.</param>
        /// <param name="intentName">The LUIS intent name of the parent action.</param>
        /// <returns>The parent action.</returns>
        public static ILuisAction BuildContextForContextualAction(ILuisAction action, out string intentName)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (!LuisActionResolver.IsContextualAction(action))
            {
                intentName = null;
                return null;
            }

            var contextType = action
                .GetType()
                .GetInterfaces()
                .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ILuisContextualAction<>))
                .GetGenericArguments()[0];

            var result = Activator.CreateInstance(contextType) as ILuisAction;
            intentName = LuisActionResolver.GetActionDefinition(result).IntentName;

            var isContextual = false;
            LuisActionResolver.IsValidContextualAction(action, result, out isContextual);

            return result;
        }

        /// <summary>
        /// Returns an action based on the LUIS intent.
        /// </summary>
        /// <param name="luisResult">The LUIS Result containing intent and extracted entities.</param>
        /// <returns>The matched action plus its parameters filled with the extracted entities, or Null if no action was matched.</returns>
        public ILuisAction ResolveActionFromLuisIntent(LuisResult luisResult)
        {
            if (luisResult == null)
            {
                throw new ArgumentNullException(nameof(luisResult));
            }

            var intentName = default(string);
            var unassigned = default(IList<EntityRecommendation>);

            return this.ResolveActionFromLuisIntent(luisResult, out intentName, out unassigned);
        }

        /// <summary>
        /// Returns an action based on the LUIS intent.
        /// </summary>
        /// <param name="luisResult">The LUIS Result containing intent and extracted entities.</param>
        /// <param name="intentName">The LUIS intent name.</param>
        /// <param name="intentEntities">The LUIS extracted entities.</param>
        /// <param name="entityExtractor">Optional: EntityExtractor to disambiguate multiple values into a single one.</param>
        /// <returns>The matched action plus its parameters filled with the extracted entities, or Null if no action was matched.</returns>
        public ILuisAction ResolveActionFromLuisIntent(
            LuisResult luisResult,
            out string intentName,
            out IList<EntityRecommendation> intentEntities,
            Func<PropertyInfo, IEnumerable<EntityRecommendation>, EntityRecommendation> entityExtractor = null)
        {
            intentEntities = default(IList<EntityRecommendation>);

            if (luisResult == null)
            {
                throw new ArgumentNullException(nameof(luisResult));
            }

            // Has Intent?
            intentName = (luisResult.TopScoringIntent ?? luisResult.Intents?.MaxBy(i => i.Score ?? 0d)).Intent;
            if (string.IsNullOrWhiteSpace(intentName))
            {
                return null;
            }

            // Set out intent entities reference
            intentEntities = luisResult.Entities;

            // Get Actions that map to this intent
            var actionType = default(Type);
            if (!this.luisActions.TryGetValue(intentName, out actionType))
            {
                return null;
            }

            // Get the action instance and check if it implements ILuisAction
            var luisAction = Activator.CreateInstance(actionType) as ILuisAction;
            if (luisAction == null)
            {
                return null;
            }

            // Try complete parameters from entities
            var properties = luisAction.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            LuisActionResolver.AssignEntitiesToMembers(luisAction, properties, luisResult.Entities, entityExtractor);

            return luisAction;
        }

        internal ILuisAction ResolveActionFromLuisIntent(LuisResult luisResult, out string intentName)
        {
            if (luisResult == null)
            {
                throw new ArgumentNullException(nameof(luisResult));
            }

            var unassigned = default(IList<EntityRecommendation>);

            return this.ResolveActionFromLuisIntent(luisResult, out intentName, out unassigned);
        }

        private static bool AssignValue(ILuisAction action, PropertyInfo property, object paramValue)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (property == null)
            {
                throw new ArgumentNullException(nameof(property));
            }

            if (paramValue == null)
            {
                throw new ArgumentNullException(nameof(paramValue));
            }

            if (property != null && property.CanWrite)
            {
                // nullable support
                var type = property.PropertyType;
                type = Nullable.GetUnderlyingType(type) ?? type;

                try
                {
                    object value;

                    // handle LUIS JObjects
                    paramValue = SanitizeInputValue(type, paramValue);

                    if (type.IsArray)
                    {
                        value = BuildArrayOfValues(action, property, type.GetElementType(), paramValue);
                    }
                    else if (type.IsEnum)
                    {
                        value = Enum.Parse(type, paramValue.ToString());
                    }
                    else
                    {
                        value = Convert.ChangeType(paramValue, type);
                    }

                    property.SetValue(action, value);

                    return true;
                }
                catch (FormatException)
                {
                    // Handle invalid values (Eg. Try Parse '2017' as a Date will fail)
                }
            }

            return false;
        }

        private static Array BuildArrayOfValues(ILuisAction action, PropertyInfo property, Type elementType, object paramValue)
        {
            var values = default(IEnumerable<object>);
            if (paramValue is IEnumerable<object>)
            {
                values = paramValue as IEnumerable<object>;
            }
            else
            {
                values = paramValue.ToString().Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(v => v.Trim());
            }

            if (values.Count() > 0)
            {
                var idx = 0;
                var result = Array.CreateInstance(elementType, values.Count());
                foreach (var value in values)
                {
                    result.SetValue(elementType.IsEnum ? Enum.Parse(elementType, value.ToString()) : Convert.ChangeType(value, elementType), idx++);
                }

                return result;
            }
            else
            {
                return null;
            }
        }

        private static object SanitizeInputValue(Type targetType, object value)
        {
            object result = value;

            // handle case where input is JArray returned from LUIS
            if (value is JArray)
            {
                var arrayOfValues = value as JArray;

                if (targetType.IsArray)
                {
                    result = arrayOfValues.AsEnumerable<object>();
                }
                else
                {
                    if (arrayOfValues.Count > 1)
                    {
                        throw new FormatException("Cannot assign multiple values to single field");
                    }

                    result = arrayOfValues[0];
                }
            }

            return result;
        }

        private static bool AssignEntitiesToMembers(
            ILuisAction action,
            IEnumerable<PropertyInfo> properties,
            IEnumerable<EntityRecommendation> entities,
            Func<PropertyInfo, IEnumerable<EntityRecommendation>, EntityRecommendation> entityExtractor = null)
        {
            var result = true;

            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (properties == null)
            {
                throw new ArgumentNullException(nameof(properties));
            }

            if (entities == null)
            {
                throw new ArgumentNullException(nameof(entities));
            }

            if (!entities.Any())
            {
                return !result;
            }

            // Cross match entities to copy resolution values for custom entities from pairs
            foreach (var group in entities.GroupBy(e => e.Entity))
            {
                if (group.Count() > 1)
                {
                    var entityToUpdate = group.FirstOrDefault(e => !BuiltInTypes.IsBuiltInType(e.Type));
                    var entityWithValue = group.FirstOrDefault(e => e.Resolution != null);
                    if (entityToUpdate != null && entityWithValue != null)
                    {
                        entityToUpdate.Resolution = entityWithValue.Resolution;
                    }
                }
            }

            foreach (var property in properties)
            {
                var matchingEntity = default(EntityRecommendation);
                var matchingEntities = default(IEnumerable<EntityRecommendation>);

                // Find using custom type from attrib if available
                var paramAttrib = property.GetCustomAttributes<LuisActionBindingParamAttribute>(true).FirstOrDefault();
                if (paramAttrib != null)
                {
                    matchingEntities = entities.Where(e => e.Type == paramAttrib.CustomType);
                }

                // Find using property name
                if (matchingEntities == null || !matchingEntities.Any())
                {
                    matchingEntities = entities.Where(e => e.Type == property.Name);
                }

                // Find using builtin type from attrib if available
                if ((matchingEntities == null || !matchingEntities.Any()) && paramAttrib != null)
                {
                    matchingEntities = entities.Where(e => e.Type == paramAttrib.BuiltinType);
                }

                // If callback available then use it
                if (matchingEntities.Count() > 1)
                {
                    if (entityExtractor != null)
                    {
                        matchingEntity = entityExtractor(property, matchingEntities);
                    }
                }
                else
                {
                    matchingEntity = matchingEntities.FirstOrDefault();
                }

                // Prioritize resolution
                if (matchingEntity != null)
                {
                    object paramValue = null;
                    if (matchingEntity.Resolution != null && matchingEntity.Resolution.Count > 0)
                    {
                        var resolutionValue = matchingEntity.Resolution.First().Value;
                        if (resolutionValue is IList<object>)
                        {
                            // multiple resolution values - datetimev2
                            var dateTimeResolutionValues = GetDateTimeValues(matchingEntity);
                            paramValue = dateTimeResolutionValues["value"];
                        }

                        if (paramValue == null)
                        {
                            // other built-in with single resolution value
                            paramValue = resolutionValue;
                        }
                    }
                    else
                    {
                        paramValue = matchingEntity.Entity;
                    }

                    result &= AssignValue(action, property, paramValue);
                }
                else if (matchingEntities.Count() > 0
                    && matchingEntities.Count(e => e.Resolution != null && e.Resolution.First().Value is JArray) == matchingEntities.Count())
                {
                    var paramValues = new JArray();

                    foreach (var currentMatchingEntity in matchingEntities)
                    {
                        var values = currentMatchingEntity.Resolution.First().Value as JArray;
                        foreach (var value in values)
                        {
                            paramValues.Add(value);
                        }
                    }

                    result &= AssignValue(action, property, paramValues);
                }
                else
                {
                    result = false;
                }
            }

            return result;
        }

        /// <summary>
        /// Get the first datetimeV2 resolution value.
        /// </summary>
        /// <param name="entity">The EntityRecomendation</param>
        /// <returns>A Dictionary with the resolution values Or null.</returns>
        public static IDictionary<string, object> GetDateTimeValues(EntityRecommendation entity)
        {
            if (!entity.Type.StartsWith("builtin.datetimeV2"))
            {
                return null;
            }

            var resolutionValues = (IList<object>)entity.Resolution["values"];

            return (IDictionary<string, object>)resolutionValues[0];
        }
    }
}
