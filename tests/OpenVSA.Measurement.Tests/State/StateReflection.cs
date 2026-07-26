using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace OpenVSA.Measurement.Tests.State
{
    /// <summary>One settable value somewhere in a state, and where it is.</summary>
    public sealed class StateLeaf
    {
        /// <summary>Creates a leaf.</summary>
        /// <param name="path">Dotted path from the root, with list indices in brackets.</param>
        /// <param name="value">The value.</param>
        public StateLeaf(string path, object value)
        {
            Path = path;
            Value = value;
        }

        /// <summary>Where it is.</summary>
        public string Path { get; }

        /// <summary>What it holds.</summary>
        public object Value { get; }

        /// <inheritdoc />
        public override string ToString() =>
            Path + " = " + (Value == null ? "(null)" : Value.ToString());
    }

    /// <summary>
    /// Walks a state by reflection, so tests can enumerate it rather than sample it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>REQ-STA-001</c> and <c>REQ-STA-005</c> both turn on this distinction: a check that names
    /// the settings it verifies passes unchanged when a setting is added without save and recall
    /// support, and that is precisely the failure both requirements are written to catch. Walking
    /// the model means a new property is covered the moment it is declared.
    /// </para>
    /// <para>
    /// Only the state model's own types are recursed into. Anything else — a string, a number, an
    /// enumerator — is a leaf, which is what makes the walk terminate and what makes the paths
    /// readable when one fails.
    /// </para>
    /// </remarks>
    public static class StateReflection
    {
        private const string StateNamespace = "OpenVSA.Measurement.State";

        /// <summary>Every settable value in a state, depth first.</summary>
        /// <param name="root">The object to walk.</param>
        /// <param name="prefix">Path prefix for the results.</param>
        /// <exception cref="ArgumentNullException"><paramref name="root"/> is null.</exception>
        public static IReadOnlyList<StateLeaf> Leaves(object root, string prefix = "")
        {
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root));
            }

            var leaves = new List<StateLeaf>();
            Walk(root, prefix, leaves);
            return leaves;
        }

        /// <summary>
        /// Moves every value in a state away from whatever it currently holds.
        /// </summary>
        /// <param name="root">The object to perturb.</param>
        /// <exception cref="ArgumentNullException"><paramref name="root"/> is null.</exception>
        /// <remarks>
        /// Each type is moved in a way that cannot land back on the original: a number is offset,
        /// a boolean inverted, an enumerator advanced with a wrap, and a string given a suffix.
        /// A value that came back equal after a save and a recall would then be one that was never
        /// carried, rather than one that happened to match.
        /// </remarks>
        public static void Perturb(object root)
        {
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root));
            }

            foreach (PropertyInfo property in PropertiesOf(root.GetType()))
            {
                object value = property.GetValue(root);

                if (value == null)
                {
                    continue;
                }

                if (IsStateType(property.PropertyType))
                {
                    Perturb(value);
                    continue;
                }

                var list = value as IList;

                if (list != null)
                {
                    foreach (object element in list)
                    {
                        if (element != null && IsStateType(element.GetType()))
                        {
                            Perturb(element);
                        }
                    }

                    continue;
                }

                if (!property.CanWrite)
                {
                    continue;
                }

                property.SetValue(root, Moved(property.PropertyType, value));
            }
        }

        /// <summary>Whether two states hold the same values everywhere.</summary>
        /// <param name="left">One state.</param>
        /// <param name="right">The other.</param>
        /// <param name="difference">Receives the first difference found, or <c>null</c>.</param>
        public static bool Same(object left, object right, out string difference)
        {
            IReadOnlyList<StateLeaf> mine = Leaves(left);
            IReadOnlyList<StateLeaf> theirs = Leaves(right);

            if (mine.Count != theirs.Count)
            {
                difference =
                    "one has " + mine.Count + " values and the other " + theirs.Count;
                return false;
            }

            for (int i = 0; i < mine.Count; i++)
            {
                if (mine[i].Path != theirs[i].Path)
                {
                    difference = "shape differs at " + mine[i].Path + " against " + theirs[i].Path;
                    return false;
                }

                if (!Equals(mine[i].Value, theirs[i].Value))
                {
                    difference =
                        mine[i].Path + " is " + Describe(mine[i].Value) + " against " +
                        Describe(theirs[i].Value);
                    return false;
                }
            }

            difference = null;
            return true;
        }

        /// <summary>Every type the state model is built from, reachable from a root.</summary>
        /// <param name="root">Where to start.</param>
        public static IReadOnlyList<Type> TypesIn(Type root)
        {
            var found = new List<Type>();
            Collect(root, found);
            return found;
        }

        private static void Collect(Type type, List<Type> found)
        {
            if (!IsStateType(type) || found.Contains(type))
            {
                return;
            }

            found.Add(type);

            foreach (PropertyInfo property in PropertiesOf(type))
            {
                Collect(property.PropertyType, found);

                foreach (Type argument in property.PropertyType.GetGenericArguments())
                {
                    Collect(argument, found);
                }
            }
        }

        private static void Walk(object node, string prefix, List<StateLeaf> leaves)
        {
            foreach (PropertyInfo property in PropertiesOf(node.GetType()))
            {
                object value = property.GetValue(node);
                string path = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;

                if (value != null && IsStateType(property.PropertyType))
                {
                    Walk(value, path, leaves);
                    continue;
                }

                var list = value as IList;

                if (list != null && !(value is string))
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        object element = list[i];
                        string elementPath = path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";

                        if (element != null && IsStateType(element.GetType()))
                        {
                            Walk(element, elementPath, leaves);
                        }
                        else
                        {
                            leaves.Add(new StateLeaf(elementPath, element));
                        }
                    }

                    continue;
                }

                leaves.Add(new StateLeaf(path, value));
            }
        }

        /// <summary>
        /// The properties of a state type, in declaration order.
        /// </summary>
        /// <remarks>
        /// Declaration order, because the comparison walks two states in parallel and pairs values
        /// by position as well as by path; reflection's default order is not guaranteed, so it is
        /// pinned by metadata token, which is.
        /// </remarks>
        private static IEnumerable<PropertyInfo> PropertiesOf(Type type) =>
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead)
                .Where(p => !Attribute.IsDefined(p, typeof(OpenVSA.Measurement.State.NotASettingAttribute)))
                .OrderBy(p => p.MetadataToken);

        private static bool IsStateType(Type type) =>
            type.Namespace != null &&
            type.Namespace.StartsWith(StateNamespace, StringComparison.Ordinal) &&
            type.IsClass &&
            type != typeof(string);

        private static object Moved(Type type, object value)
        {
            if (type == typeof(string))
            {
                return (string)value + "-moved";
            }

            if (type == typeof(bool))
            {
                return !(bool)value;
            }

            if (type.IsEnum)
            {
                Array values = Enum.GetValues(type);
                int index = Array.IndexOf(values, value);
                return values.GetValue((index + 1) % values.Length);
            }

            if (type == typeof(int))
            {
                return (int)value + 7;
            }

            if (type == typeof(uint))
            {
                return (uint)value + 7u;
            }

            if (type == typeof(long))
            {
                return (long)value + 7L;
            }

            if (type == typeof(double))
            {
                double d = (double)value;
                return Math.Abs(d) > 1e-12 ? d * 1.5 : 3.25;
            }

            if (type == typeof(float))
            {
                float f = (float)value;
                return Math.Abs(f) > 1e-6f ? f * 1.5f : 3.25f;
            }

            throw new NotSupportedException(
                "The state model gained a property of type " + type.Name +
                ", which this walk does not know how to move away from its default. Teach it, " +
                "rather than exempting the property - an unmoved value would make the save and " +
                "recall test pass without testing anything.");
        }

        private static string Describe(object value) =>
            value == null ? "(null)" : value.ToString();
    }
}
