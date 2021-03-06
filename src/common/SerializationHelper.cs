﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit.Abstractions;
using Xunit.Serialization;

namespace Xunit.Sdk
{
    /// <summary>
    /// Serializes and de-serializes objects
    /// </summary>
    internal static class SerializationHelper
    {
        /// <summary>
        /// De-serializes an object.
        /// </summary>
        /// <typeparam name="T">The type of the object</typeparam>
        /// <param name="serializedValue">The object's serialized value</param>
        /// <returns>The de-serialized object</returns>
        public static T Deserialize<T>(string serializedValue)
        {
            if (serializedValue == null)
                throw new ArgumentNullException("serializedValue");

            var pieces = serializedValue.Split(new[] { ':' }, 2);
            if (pieces.Length != 2)
                throw new ArgumentException("De-serialized string is in the incorrect format.");

            var deserializedType = GetType(pieces[0]);
            if (deserializedType == null)
                throw new ArgumentException("Could not load type " + pieces[0], "serializedValue");

            if (!typeof(IXunitSerializable).IsAssignableFrom(deserializedType))
                throw new ArgumentException("Cannot de-serialize an object that does not implement " + typeof(IXunitSerializable).FullName, "T");

            var obj = XunitSerializationInfo.Deserialize(deserializedType, pieces[1]);
            if (obj is XunitSerializationInfo.ArraySerializer)
                obj = ((XunitSerializationInfo.ArraySerializer)obj).ArrayData;

            return (T)obj;
        }

        /// <summary>
        /// Serializes an object.
        /// </summary>
        /// <param name="value">The value to serialize</param>
        /// <returns>The serialized value</returns>
        public static string Serialize(object value)
        {
            if (value == null)
                throw new ArgumentNullException("value");

            var array = value as object[];
            if (array != null)
                value = new XunitSerializationInfo.ArraySerializer(array);

            var serializable = value as IXunitSerializable;
            if (serializable == null)
                throw new ArgumentException("Cannot serialize an object that does not implement " + typeof(IXunitSerializable).FullName, "value");

            var serializationInfo = new XunitSerializationInfo(serializable);
            return String.Format("{0}:{1}", GetTypeNameForSerialization(value.GetType()), serializationInfo.ToSerializedString());
        }

        /// <summary>
        /// Converts an assembly qualified type name into a <see cref="Type"/> object.
        /// </summary>
        /// <param name="assemblyQualifiedTypeName">The assembly qualified type name.</param>
        /// <returns>The instance of the <see cref="Type"/>, if available; <c>null</c>, otherwise.</returns>
        public static Type GetType(string assemblyQualifiedTypeName)
        {
            var firstOpenSquare = assemblyQualifiedTypeName.IndexOf('[');
            if (firstOpenSquare > 0)
            {
                var backtick = assemblyQualifiedTypeName.IndexOf('`');
                if (backtick > 0 && backtick < firstOpenSquare)
                {
                    // Run the string looking for the matching closing square brace. Can't just assume the last one
                    // is the end, since the type could be trailed by array designators.
                    var depth = 1;
                    var lastOpenSquare = firstOpenSquare + 1;
                    var sawNonArrayDesignator = false;
                    for (; depth > 0 && lastOpenSquare < assemblyQualifiedTypeName.Length; ++lastOpenSquare)
                    {
                        switch (assemblyQualifiedTypeName[lastOpenSquare])
                        {
                            case '[': ++depth; break;
                            case ']': --depth; break;
                            case ',': break;
                            default: sawNonArrayDesignator = true; break;
                        }
                    }

                    if (sawNonArrayDesignator)
                    {
                        if (depth != 0)  // Malformed, because we never closed what we opened
                            return null;

                        var genericArgument = assemblyQualifiedTypeName.Substring(firstOpenSquare + 1, lastOpenSquare - firstOpenSquare - 2);  // Strip surrounding [ and ]
                        var innerTypeNames = SplitAtOuterCommas(genericArgument).Select(x => x.Substring(1, x.Length - 2)).ToArray();  // Strip surrounding [ and ] from each type name
                        var innerTypes = innerTypeNames.Select(GetType).ToArray();
                        if (innerTypes.Any(t => t == null))
                            return null;

                        var genericDefinitionName = assemblyQualifiedTypeName.Substring(0, firstOpenSquare) + assemblyQualifiedTypeName.Substring(lastOpenSquare);
                        var genericDefinition = GetType(genericDefinitionName);
                        if (genericDefinition == null)
                            return null;

                        // Push array ranks so we can get down to the actual generic definition
                        var arrayRanks = new Stack<int>();
                        while (genericDefinition.IsArray)
                        {
                            arrayRanks.Push(genericDefinition.GetArrayRank());
                            genericDefinition = genericDefinition.GetElementType();
                        }

                        var closedGenericType = genericDefinition.MakeGenericType(innerTypes);
                        while (arrayRanks.Count > 0)
                        {
                            var rank = arrayRanks.Pop();
                            if (rank > 1)
                                closedGenericType = closedGenericType.MakeArrayType(rank);
                            else
                                closedGenericType = closedGenericType.MakeArrayType();
                        }

                        return closedGenericType;
                    }
                }
            }

            var parts = SplitAtOuterCommas(assemblyQualifiedTypeName).Select(x => x.Trim()).ToList();
            if (parts.Count == 0)
                return null;

            if (parts.Count == 1)
                return Type.GetType(parts[0]);

            return GetType(parts[1], parts[0]);
        }

        /// <summary>
        /// Converts an assembly name + type name into a <see cref="Type"/> object.
        /// </summary>
        /// <param name="assemblyName">The assembly name.</param>
        /// <param name="typeName">The type name.</param>
        /// <returns>The instance of the <see cref="Type"/>, if available; <c>null</c>, otherwise.</returns>
        public static Type GetType(string assemblyName, string typeName)
        {
            if (assemblyName.EndsWith(ExecutionHelper.SubstitutionToken))
                assemblyName = assemblyName.Substring(0, assemblyName.Length - ExecutionHelper.SubstitutionToken.Length + 1) + ExecutionHelper.PlatformSpecificAssemblySuffix;

#if WINDOWS_PHONE_APP || WINDOWS_PHONE
            Assembly assembly = null;
            try
            {
                // Make sure we only use the short form for WPA81
                var an = new AssemblyName(assemblyName);
                assembly = Assembly.Load(new AssemblyName { Name = an.Name });

            }
            catch { }
#else
            // Support both long name ("assembly, version=x.x.x.x, etc.") and short name ("assembly")
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.FullName == assemblyName || a.GetName().Name == assemblyName);
            if (assembly == null)
            {
                try
                {
                    assembly = Assembly.Load(assemblyName);
                }
                catch { }
            }
#endif

            if (assembly == null)
                return null;

            return assembly.GetType(typeName);
        }

        /// <summary>
        /// Gets an assembly qualified type name for serialization, with special dispensation for types which
        /// originate in the execution assembly.
        /// </summary>
        public static string GetTypeNameForSerialization(Type type)
        {
            if (typeof(Type).IsAssignableFrom(type))
                type = typeof(Type);

            var typeName = type.FullName;
            var assemblyName = type.GetAssembly().FullName.Split(',')[0];

            var arrayRanks = new Stack<int>();
            while (type.IsArray)
            {
                arrayRanks.Push(type.GetArrayRank());
                type = type.GetElementType();
            }

            if (type.IsGenericType())
            {
                var typeDefinition = type.GetGenericTypeDefinition();
                var innerTypes = type.GetGenericArguments().Select(t => String.Format("[{0}]", GetTypeNameForSerialization(t))).ToArray();
                typeName = String.Format("{0}[{1}]", typeDefinition.FullName, String.Join(",", innerTypes));

                while (arrayRanks.Count > 0)
                {
                    typeName += '[';
                    for (var commas = arrayRanks.Pop() - 1; commas > 0; --commas)
                        typeName += ',';
                    typeName += ']';
                }
            }

            if (String.Equals(assemblyName, "mscorlib", StringComparison.OrdinalIgnoreCase))
                return typeName;

            // If this is a platform specific assembly, strip off the trailing . and name and replace it with the token
            if (type.GetAssembly().GetCustomAttributes().FirstOrDefault(a => a != null && a.GetType().FullName == "Xunit.Sdk.PlatformSpecificAssemblyAttribute") != null)
                assemblyName = assemblyName.Substring(0, assemblyName.LastIndexOf('.')) + ExecutionHelper.SubstitutionToken;

            return String.Format("{0}, {1}", typeName, assemblyName);
        }

        private static IList<string> SplitAtOuterCommas(string value)
        {
            var results = new List<string>();

            var startIndex = 0;
            var endIndex = 0;
            var depth = 0;

            for (; endIndex < value.Length; ++endIndex)
            {
                switch (value[endIndex])
                {
                    case '[': ++depth; break;
                    case ']': --depth; break;
                    case ',':
                        if (depth == 0)
                        {
                            results.Add(value.Substring(startIndex, endIndex - startIndex));
                            startIndex = endIndex + 1;
                        }
                        break;
                }
            }

            if (depth != 0 || startIndex >= endIndex)
                return new List<string>();

            results.Add(value.Substring(startIndex, endIndex - startIndex));
            return results;
        }
    }
}
