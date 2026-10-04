#nullable enable
namespace SignalFish.Client.Adapters.Mirror.Editor
{
    using System;
    using System.Linq;
    using UnityEditor;
    using UnityEditor.Build;
    using UnityEditor.Compilation;

    /// <summary>
    /// Detects the Mirror asset and toggles the <c>SIGNALFISH_MIRROR</c>
    /// scripting define across every build target. Mirror ships as an
    /// asset (Asset Store or a zip under <c>Assets/</c>) with no UPM
    /// package, so there is no <c>versionDefines</c> resource to key on:
    /// this detector is the define's owner, and the adapter asmdef's
    /// defineConstraints keep the assembly inert without it.
    ///
    /// The probe is the compiled <c>Mirror</c> assembly itself, so a
    /// fresh install needs one editor compile before the adapter lights
    /// up (the standard cost of define detectors).
    /// </summary>
    [InitializeOnLoad]
    internal static class SignalFishMirrorDefineDetector
    {
        private const string Define = "SIGNALFISH_MIRROR";

        /// <summary>The adapter pins Mirror's core assembly name.</summary>
        private const string MirrorAssemblyName = "Mirror";

        private const double RescanIntervalSeconds = 5.0;

        private static double _nextRescanTime;

        static SignalFishMirrorDefineDetector()
        {
            EditorApplication.update += Rescan;
        }

        private static void Rescan()
        {
            if (EditorApplication.timeSinceStartup < _nextRescanTime)
            {
                return;
            }

            _nextRescanTime = EditorApplication.timeSinceStartup + RescanIntervalSeconds;
            Apply(MirrorIsPresent());
        }

        private static bool MirrorIsPresent()
        {
            return CompilationPipeline
                .GetAssemblies()
                .Any(assembly =>
                    string.Equals(assembly.name, MirrorAssemblyName, StringComparison.Ordinal)
                );
        }

        private static void Apply(bool mirrorPresent)
        {
            /*
                Valid build target groups vary by Unity version and license;
                an invalid one throws and is skipped rather than failing the
                whole pass.
            */
            foreach (BuildTargetGroup group in Enum.GetValues(typeof(BuildTargetGroup)))
            {
                if (group == BuildTargetGroup.Unknown)
                {
                    continue;
                }

                try
                {
                    NamedBuildTarget target = NamedBuildTarget.FromBuildTargetGroup(group);
                    string current = PlayerSettings.GetScriptingDefineSymbols(target);
                    string updated = Toggle(current, mirrorPresent);
                    if (updated != current)
                    {
                        PlayerSettings.SetScriptingDefineSymbols(target, updated);
                    }
                }
                catch (ArgumentException)
                {
                    // Not a valid scripting group on this Unity version.
                }
                catch (NotSupportedException)
                {
                    // Not a valid scripting group on this Unity version.
                }
            }
        }

        private static string Toggle(string defines, bool mirrorPresent)
        {
            string[] parts = defines
                .Split(';')
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .Where(part => part != Define)
                .ToArray();
            if (!mirrorPresent)
            {
                /*
                    The define without Mirror would dangle the adapter's
                    Mirror reference; removing it keeps the assembly skipped.
                */
                return string.Join(";", parts);
            }

            return string.Join(";", parts.Concat(new[] { Define }));
        }
    }
}
