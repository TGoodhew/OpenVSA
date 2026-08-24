using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace OpenVSA.Measurement.State
{
    /// <summary>
    /// Raised when a state file cannot be read.
    /// </summary>
    [Serializable]
    public class StateFormatException : Exception
    {
        /// <summary>Creates the exception.</summary>
        public StateFormatException()
            : base("The state file could not be read.")
        {
        }

        /// <summary>Creates the exception with a message.</summary>
        /// <param name="message">What is wrong with the file.</param>
        public StateFormatException(string message)
            : base(message)
        {
        }

        /// <summary>Creates the exception with a message and an inner exception.</summary>
        /// <param name="message">What is wrong with the file.</param>
        /// <param name="inner">The underlying cause.</param>
        public StateFormatException(string message, Exception inner)
            : base(message, inner)
        {
        }

        /// <summary>Deserialisation constructor.</summary>
        /// <param name="info">Serialisation data.</param>
        /// <param name="context">Streaming context.</param>
        protected StateFormatException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Reads and writes setups as versioned JSON (<c>REQ-STA-003</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Text, not the reference product's opaque binary.</strong> There is no
    /// interoperability requirement, and a readable format is worth a great deal for the things
    /// setups are actually used for: putting a measurement under version control, sending one to
    /// somebody who cannot reproduce a result, and reading what a file says when the software that
    /// wrote it will not load it.
    /// </para>
    /// <para>
    /// <strong>Unknown members survive a round trip.</strong> A file written by later software,
    /// loaded here and saved again, comes back with its unrecognised members exactly as they were.
    /// Without that an older build is a one-way door: opening a colleague's setup would silently
    /// discard everything it did not understand, and the loss would only surface later, on their
    /// machine.
    /// </para>
    /// </remarks>
    public static class StateFile
    {
        /// <summary>The extension a state file is written with.</summary>
        public const string Extension = ".ovsa-state.json";

        /// <summary>
        /// What a state does <em>not</em> contain, in the words the save dialog must show
        /// (<c>REQ-STA-002</c>).
        /// </summary>
        /// <remarks>
        /// Here rather than in the dialog's markup so that the exclusions and the text describing
        /// them cannot drift apart, and so a test can assert all four are named.
        /// </remarks>
        public const string ExclusionNotice =
            "A saved state does not include recordings, math functions, data registers or display " +
            "preferences. Those are saved and recalled separately.";

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Formatting = Formatting.Indented,

            // Written out even at their defaults. A setup file that omitted everything left at a
            // default would be shorter and far less useful: the point of a readable format is that
            // it says what the measurement was set to, including the parts nobody changed.
            NullValueHandling = NullValueHandling.Include,
            DefaultValueHandling = DefaultValueHandling.Include,

            // Replaced, not appended to. The state model gives its collections sensible defaults -
            // one trace window, one trace - and the serialiser's default behaviour is to reuse an
            // existing collection and add to it, so a loaded state would come back with its
            // defaults still in front of what the file actually said.
            ObjectCreationHandling = ObjectCreationHandling.Replace,
        };

        /// <summary>
        /// Writes a state as indented JSON.
        /// </summary>
        /// <param name="state">The state to write.</param>
        /// <returns>The JSON text.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
        public static string Write(ApplicationState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            state.SchemaVersion = ApplicationState.CurrentSchemaVersion;
            state.WrittenUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            var document = JObject.FromObject(state, JsonSerializer.Create(Settings));

            // The preserved members are the loader's bookkeeping, not part of the state.
            document.Remove(NameOf(nameof(ApplicationState.UnknownMembersJson)));

            if (!string.IsNullOrEmpty(state.UnknownMembersJson))
            {
                try
                {
                    // Merged back at whatever depth they were found, so a member this software has
                    // never heard of comes out of the round trip where it went in.
                    document.Merge(
                        JObject.Parse(state.UnknownMembersJson),
                        new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Merge });
                }
                catch (JsonException failure)
                {
                    throw new StateFormatException(
                        "The preserved members are not valid JSON, so the state cannot be written " +
                        "without losing them.",
                        failure);
                }
            }

            return document.ToString(Formatting.Indented);
        }

        /// <summary>
        /// Reads a state from JSON.
        /// </summary>
        /// <param name="json">The JSON text.</param>
        /// <returns>The state, with any unrecognised members preserved.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="json"/> is null or empty.</exception>
        /// <exception cref="StateFormatException">The text is not a readable state.</exception>
        public static ApplicationState Read(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                throw new ArgumentNullException(nameof(json));
            }

            JObject document;

            try
            {
                document = JObject.Parse(json);
            }
            catch (JsonException failure)
            {
                throw new StateFormatException("The state file is not valid JSON.", failure);
            }

            int version = VersionOf(document);

            if (version < ApplicationState.OldestReadableSchemaVersion)
            {
                throw new StateFormatException(
                    "This state was written under schema version " +
                    version.ToString(CultureInfo.CurrentCulture) +
                    ", which this software no longer reads; the oldest it reads is " +
                    ApplicationState.OldestReadableSchemaVersion.ToString(CultureInfo.CurrentCulture) +
                    ".");
            }

            document = Migrate(document, version);

            ApplicationState state;

            try
            {
                state = document.ToObject<ApplicationState>(JsonSerializer.Create(Settings));
            }
            catch (JsonException failure)
            {
                throw new StateFormatException(
                    "The state file is JSON but not a state: " + failure.Message, failure);
            }

            if (state == null)
            {
                throw new StateFormatException("The state file holds nothing.");
            }

            // What this software understood, re-expressed; anything in the file that is not in it
            // is a member from a later schema, and is kept.
            var understood = JObject.FromObject(state, JsonSerializer.Create(Settings));
            understood.Remove(NameOf(nameof(ApplicationState.UnknownMembersJson)));

            JObject unknown = Surplus(document, understood);
            state.UnknownMembersJson = unknown.HasValues ? unknown.ToString(Formatting.Indented) : string.Empty;

            return state;
        }

        /// <summary>
        /// Saves a state to a file.
        /// </summary>
        /// <param name="state">The state.</param>
        /// <param name="path">Where to write it.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public static void Save(ApplicationState state, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentNullException(nameof(path));
            }

            File.WriteAllText(path, Write(state));
        }

        /// <summary>
        /// Loads a state from a file.
        /// </summary>
        /// <param name="path">The file.</param>
        /// <returns>The state.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null or empty.</exception>
        /// <exception cref="StateFormatException">The file is not a readable state.</exception>
        public static ApplicationState Load(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentNullException(nameof(path));
            }

            return Read(File.ReadAllText(path));
        }

        /// <summary>
        /// Brings a document up to the current schema.
        /// </summary>
        /// <param name="document">The document as it was read.</param>
        /// <param name="version">The version it declares.</param>
        /// <returns>The document, at the current schema.</returns>
        /// <remarks>
        /// <para>
        /// One step per version, applied in order, so that a file two versions old goes through
        /// the same transformations a file one version old did — rather than each migration having
        /// to know about every version before it.
        /// </para>
        /// <para>
        /// There is nothing to do yet: version 1 is the current schema. The structure is here
        /// because <c>REQ-STA-003</c> requires migration to be documented, and a migration path
        /// invented at the moment it is first needed is one written under pressure.
        /// </para>
        /// </remarks>
        private static JObject Migrate(JObject document, int version)
        {
            for (int from = version; from < ApplicationState.CurrentSchemaVersion; from++)
            {
                switch (from)
                {
                    case 1:
                        // Version 2 added the demodulator's settings to each measurement. A
                        // version 1 file has none, and the model's own defaults supply them --
                        // which is why this step transforms nothing. It exists rather than being
                        // skipped because the alternative is a version number nobody can trace to
                        // a change, and because the next migration has to run after this one
                        // whether or not this one does any work.
                        break;

                    case 2:
                        // Version 3 added REQ-DEM-012's differential reference to each
                        // measurement's demodulator settings. A version 2 file has none, and the
                        // model's default -- follow the format -- is exactly what a version 2 file
                        // meant, so this step transforms nothing either.
                        break;

                    case 3:
                        // Version 4 added REQ-DEM-011's bit mapping and the definition of a
                        // user-defined constellation. A version 3 file has neither; the natural
                        // mapping is what its formats meant, and no definition is what "a format
                        // from the catalogue" means. Transforms nothing, for the third time -- and
                        // is here for the reason the first one was.
                        break;

                    default:
                        throw new StateFormatException(
                            "No migration from schema version " +
                            from.ToString(CultureInfo.CurrentCulture) + ".");
                }
            }

            return document;
        }

        private static int VersionOf(JObject document)
        {
            JToken version = document[NameOf(nameof(ApplicationState.SchemaVersion))];

            if (version == null || version.Type != JTokenType.Integer)
            {
                throw new StateFormatException(
                    "The file carries no schema version, so it is not an OpenVSA state.");
            }

            return version.Value<int>();
        }

        /// <summary>
        /// What is in <paramref name="file"/> and not in <paramref name="understood"/>.
        /// </summary>
        /// <remarks>
        /// Recursive, so a member added inside a measurement by later software is kept as
        /// faithfully as one added at the top level. Arrays are compared element by element,
        /// because a state's arrays are positional — the third trace is the third trace — and
        /// treating them as opaque would lose everything a later schema added to any of them.
        /// </remarks>
        private static JObject Surplus(JObject file, JObject understood)
        {
            var surplus = new JObject();

            foreach (KeyValuePair<string, JToken> member in file)
            {
                JToken mine = understood[member.Key];

                if (mine == null)
                {
                    surplus[member.Key] = member.Value.DeepClone();
                    continue;
                }

                JToken nested = SurplusOf(member.Value, mine);

                if (nested != null)
                {
                    surplus[member.Key] = nested;
                }
            }

            return surplus;
        }

        private static JToken SurplusOf(JToken theirs, JToken mine)
        {
            var theirObject = theirs as JObject;
            var myObject = mine as JObject;

            if (theirObject != null && myObject != null)
            {
                JObject nested = Surplus(theirObject, myObject);
                return nested.HasValues ? nested : null;
            }

            var theirArray = theirs as JArray;
            var myArray = mine as JArray;

            if (theirArray != null && myArray != null)
            {
                var elements = new JArray();
                bool any = false;

                for (int i = 0; i < theirArray.Count; i++)
                {
                    JToken element = i < myArray.Count
                        ? SurplusOf(theirArray[i], myArray[i])
                        : theirArray[i].DeepClone();

                    if (element == null)
                    {
                        // A placeholder, so later elements stay at their own index when the
                        // surplus is merged back.
                        elements.Add(new JObject());
                    }
                    else
                    {
                        elements.Add(element);
                        any = true;
                    }
                }

                return any ? elements : null;
            }

            return null;
        }

        private static string NameOf(string property) =>
            char.ToLowerInvariant(property[0]) + property.Substring(1);
    }
}
