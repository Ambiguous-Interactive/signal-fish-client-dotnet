#nullable enable
namespace SignalFish.Client.Adapters.Ngo.Editor
{
    using System;
    using System.Linq;
    using UnityEditor;
    using UnityEditor.Build;
    using UnityEditor.Compilation;

    /// <summary>
    /// Detects Netcode for GameObjects and toggles the
    /// <c>SIGNALFISH_NGO</c> scripting define across every build target.
    /// The adapter asmdef's <c>versionDefines</c> key off NGO's UPM
    /// package and remain the primary path; this detector only fills the
    /// vendored gap, where NGO is a directory of asmdefs no version
    /// define can see. Both mechanisms pin the same define, so whichever
    /// fires first (or both) lands the same contract.
    ///
    /// The probe is the compiled <c>Unity.Netcode.Runtime</c> assembly or
    /// the UPM package marker, so a fresh install needs one editor
    /// compile before the adapter lights up (the standard cost of define
    /// detectors).
    /// </summary>
    [InitializeOnLoad]
    internal static class SignalFishNgoDefineDetector
    {
        private const string Define = "SIGNALFISH_NGO";

        /// <summary>The adapter pins NGO's core assembly name.</summary>
        private const string NgoAssemblyName = "Unity.Netcode.Runtime";

        /// <summary>The adapter pins NGO's UPM package name.</summary>
        private const string NgoPackageName = "com.unity.netcode.gameobjects";

        private const double RescanIntervalSeconds = 5.0;

        private static double _nextRescanTime;

        static SignalFishNgoDefineDetector()
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
            Apply(NgoIsPresent());
        }

        private static bool NgoIsPresent()
        {
            if (PackageInfo.FindForAssetPath($"Packages/{NgoPackageName}") != null)
            {
                return true;
            }

            return CompilationPipeline
                .GetAssemblies()
                .Any(assembly =>
                    string.Equals(assembly.name, NgoAssemblyName, StringComparison.Ordinal)
                );
        }

        private static void Apply(bool ngoPresent)
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
                    string updated = Toggle(current, ngoPresent);
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

        private static string Toggle(string defines, bool ngoPresent)
        {
            string[] parts = defines
                .Split(';')
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .Where(part => part != Define)
                .ToArray();
            if (!ngoPresent)
            {
                /*
                    The define without NGO would dangle the adapter's
                    Unity.Netcode.Runtime reference; removing it keeps the
                    assembly skipped.
                */
                return string.Join(";", parts);
            }

            return string.Join(";", parts.Concat(new[] { Define }));
        }
    }
}
