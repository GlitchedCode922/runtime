﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.NET.Sdk.WebAssembly;
using WasmAppBuilder;

namespace Microsoft.WebAssembly.Build.Tasks;

public class WasmAppBuilder : WasmAppBuilderBaseTask
{
    public ITaskItem[]? RemoteSources { get; set; }
    public bool IncludeThreadsWorker { get; set; }
    public int PThreadPoolInitialSize { get; set; }
    public int PThreadPoolUnusedSize { get; set; }
    public bool UseWebcil { get; set; }
    public bool WasmIncludeFullIcuData { get; set; }
    public string? WasmIcuDataFileName { get; set; }
    public string? RuntimeAssetsLocation { get; set; }
    public bool CacheBootResources { get; set; }
    public string? DebugLevel { get; set; }
    public bool IsPublish { get; set; }
    public bool IsAot { get; set; }
    public bool IsMultiThreaded { get; set; }

    [Required]
    public string ConfigFileName { get; set; } = default!;

    // <summary>
    // Extra json elements to add to _framework/blazor.boot.json
    //
    // Metadata:
    // - Value: can be a number, bool, quoted string, or json string
    //
    // Examples:
    //      <WasmExtraConfig Include="enableProfiler" Value="true" />
    //      <WasmExtraConfig Include="json" Value="{ &quot;abc&quot;: 4 }" />
    //      <WasmExtraConfig Include="string_val" Value="&quot;abc&quot;" />
    //       <WasmExtraConfig Include="string_with_json" Value="&quot;{ &quot;abc&quot;: 4 }&quot;" />
    // </summary>
    public ITaskItem[]? ExtraConfig { get; set; }

    /// <summary>
    /// Environment variables to set in the boot.json file.
    /// </summary>
    public ITaskItem[]? EnvVariables { get; set; }

    /// <summary>
    /// List of profilers to use.
    /// </summary>
    public string[]? Profilers { get; set; }

    protected override bool ValidateArguments()
    {
        if (!base.ValidateArguments())
            return false;

        if (!InvariantGlobalization && (IcuDataFileNames == null || IcuDataFileNames.Length == 0))
            throw new LogAsErrorException($"{nameof(IcuDataFileNames)} property shouldn't be empty when {nameof(InvariantGlobalization)}=false");

        if (Assemblies.Length == 0)
        {
            Log.LogError("Cannot build Wasm app without any assemblies");
            return false;
        }

        return true;
    }

    private GlobalizationMode GetGlobalizationMode()
    {
        // Invariant has always precedence
        if (InvariantGlobalization)
            return GlobalizationMode.Invariant;

        // If user provided a path to a custom ICU data file, use it
        if (!string.IsNullOrEmpty(WasmIcuDataFileName))
            return GlobalizationMode.Custom;

        // If user requested to include full ICU data, use it
        if (WasmIncludeFullIcuData)
            return GlobalizationMode.All;

        // Otherwise, use sharded mode
        return GlobalizationMode.Sharded;
    }

    protected override bool ExecuteInternal()
    {
        var helper = new BootJsonBuilderHelper(Log, DebugLevel!, IsMultiThreaded, IsPublish);
        var logAdapter = new LogAdapter(Log);

        if (!ValidateArguments())
            return false;

        var _assemblies = new List<string>();
        foreach (var asm in Assemblies!)
        {
            if (!_assemblies.Contains(asm))
                _assemblies.Add(asm);
        }
        MainAssemblyName = Path.GetFileName(MainAssemblyName);

        var bootConfig = new BootJsonData()
        {
            mainAssemblyName = MainAssemblyName,
            globalizationMode = GetGlobalizationMode().ToString().ToLowerInvariant()
        };

        if (CacheBootResources)
            bootConfig.cacheBootResources = CacheBootResources;

        // Create app
        var runtimeAssetsPath = !string.IsNullOrEmpty(RuntimeAssetsLocation)
            ? Path.Combine(AppDir, RuntimeAssetsLocation)
            : AppDir;

        Log.LogMessage(MessageImportance.Low, $"Runtime assets output path {runtimeAssetsPath}");

        Directory.CreateDirectory(AppDir!);
        Directory.CreateDirectory(runtimeAssetsPath);

        if (UseWebcil)
            Log.LogMessage(MessageImportance.Normal, "Converting assemblies to Webcil");

        int baseDebugLevel = helper.GetDebugLevel(false);

        foreach (var assembly in _assemblies)
        {
            if (UseWebcil)
            {
                using TempFileName tmpWebcil = new();
                var webcilWriter = Microsoft.WebAssembly.Build.Tasks.WebcilConverter.FromPortableExecutable(inputPath: assembly, outputPath: tmpWebcil.Path, logger: logAdapter);
                webcilWriter.ConvertToWebcil();
                var finalWebcil = Path.Combine(runtimeAssetsPath, Path.ChangeExtension(Path.GetFileName(assembly), Utils.WebcilInWasmExtension));
                if (Utils.CopyIfDifferent(tmpWebcil.Path, finalWebcil, useHash: true))
                    Log.LogMessage(MessageImportance.Low, $"Generated {finalWebcil} .");
                else
                    Log.LogMessage(MessageImportance.Low, $"Skipped generating {finalWebcil} as the contents are unchanged.");
                _fileWrites.Add(finalWebcil);
            }
            else
            {
                FileCopyChecked(assembly, Path.Combine(runtimeAssetsPath, Path.GetFileName(assembly)), "Assemblies");
            }
            if (baseDebugLevel != 0)
            {
                var pdb = assembly;
                pdb = Path.ChangeExtension(pdb, ".pdb");
                if (File.Exists(pdb))
                    FileCopyChecked(pdb, Path.Combine(runtimeAssetsPath, Path.GetFileName(pdb)), "Assemblies");
            }
        }

        foreach (ITaskItem item in NativeAssets)
        {
            var name = Path.GetFileName(item.ItemSpec);
            var dest = Path.Combine(runtimeAssetsPath, name);
            if (!FileCopyChecked(item.ItemSpec, dest, "NativeAssets"))
                return false;

            if (!IncludeThreadsWorker && name == "dotnet.native.worker.mjs")
                continue;

            if (name == "dotnet.runtime.js.map" || name == "dotnet.js.map" || name == "dotnet.diagnostics.js.map")
            {
                Log.LogMessage(MessageImportance.Low, $"Skipping {item.ItemSpec} from boot config");
                continue;
            }

            var itemHash = Utils.ComputeIntegrity(item.ItemSpec);

            Dictionary<string, string>? resourceList = helper.GetNativeResourceTargetInBootConfig(bootConfig, name);
            if (resourceList != null)
                resourceList[name] = itemHash;
        }

        string packageJsonPath = Path.Combine(AppDir, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            var json = @"{ ""type"":""module"" }";
            File.WriteAllText(packageJsonPath, json);
        }

        ResourcesData resources = (ResourcesData)bootConfig.resources;

        foreach (var assembly in _assemblies)
        {
            string assemblyPath = assembly;
            var bytes = File.ReadAllBytes(assemblyPath);
            // for the is IL IsAssembly check we need to read the bytes from the original DLL
            if (!Utils.IsManagedAssembly(bytes))
            {
                Log.LogMessage(MessageImportance.Low, "Skipping non-assembly file: " + assemblyPath);
            }
            else
            {
                if (UseWebcil)
                {
                    assemblyPath = Path.Combine(runtimeAssetsPath, Path.ChangeExtension(Path.GetFileName(assembly), Utils.WebcilInWasmExtension));
                    // For the hash, read the bytes from the webcil file, not the dll file.
                    bytes = File.ReadAllBytes(assemblyPath);
                }

                var assemblyName = Path.GetFileName(assemblyPath);
                bool isCoreAssembly = IsAot || helper.IsCoreAssembly(assemblyName);

                var assemblyList = isCoreAssembly ? resources.coreAssembly : resources.assembly;
                assemblyList[assemblyName] = Utils.ComputeIntegrity(bytes);

                if (baseDebugLevel != 0)
                {
                    var pdb = Path.ChangeExtension(assembly, ".pdb");
                    if (File.Exists(pdb))
                    {
                        if (isCoreAssembly)
                            resources.corePdb ??= new();
                        else
                            resources.pdb ??= new();

                        var pdbList = isCoreAssembly ? resources.corePdb : resources.pdb;
                        pdbList[Path.GetFileName(pdb)] = Utils.ComputeIntegrity(pdb);
                    }
                }
            }
        }

        bootConfig.debugLevel = helper.GetDebugLevel(resources.pdb?.Count > 0);

        ProcessSatelliteAssemblies(args =>
        {
            resources.satelliteResources ??= new();

            string name = Path.GetFileName(args.fullPath);
            string cultureDirectory = Path.Combine(runtimeAssetsPath, args.culture);
            Directory.CreateDirectory(cultureDirectory);
            if (UseWebcil)
            {
                using TempFileName tmpWebcil = new();
                var webcilWriter = Microsoft.WebAssembly.Build.Tasks.WebcilConverter.FromPortableExecutable(inputPath: args.fullPath, outputPath: tmpWebcil.Path, logger: logAdapter);
                webcilWriter.ConvertToWebcil();
                var finalWebcil = Path.Combine(cultureDirectory, Path.ChangeExtension(name, Utils.WebcilInWasmExtension));
                if (Utils.CopyIfDifferent(tmpWebcil.Path, finalWebcil, useHash: true))
                    Log.LogMessage(MessageImportance.Low, $"Generated {finalWebcil} .");
                else
                    Log.LogMessage(MessageImportance.Low, $"Skipped generating {finalWebcil} as the contents are unchanged.");
                _fileWrites.Add(finalWebcil);

                if (!resources.satelliteResources.TryGetValue(args.culture, out var cultureSatelliteResources))
                    resources.satelliteResources[args.culture] = cultureSatelliteResources = new();

                cultureSatelliteResources[Path.GetFileName(finalWebcil)] = Utils.ComputeIntegrity(finalWebcil);
            }
            else
            {
                var satellitePath = Path.Combine(cultureDirectory, name);
                FileCopyChecked(args.fullPath, satellitePath, "SatelliteAssemblies");

                if (!resources.satelliteResources.TryGetValue(args.culture, out var cultureSatelliteResources))
                    resources.satelliteResources[args.culture] = cultureSatelliteResources = new();

                cultureSatelliteResources[name] = Utils.ComputeIntegrity(satellitePath);
            }
        });

        if (FilesToIncludeInFileSystem.Length > 0)
        {
            string supportFilesDir = Path.Combine(runtimeAssetsPath, "supportFiles");
            Directory.CreateDirectory(supportFilesDir);

            var i = 0;
            StringDictionary targetPathTable = new();
            var vfs = new Dictionary<string, Dictionary<string, string>>();
            var coreVfs = new Dictionary<string, Dictionary<string, string>>();
            foreach (var item in FilesToIncludeInFileSystem)
            {
                string? targetPath = item.GetMetadata("TargetPath");
                string? loadingStage = item.GetMetadata("LoadingStage");
                if (string.IsNullOrEmpty(targetPath))
                {
                    targetPath = Path.GetFileName(item.ItemSpec);
                }

                // We normalize paths from `\` to `/` as MSBuild items could use `\`.
                targetPath = targetPath.Replace('\\', '/');
                if (targetPathTable.ContainsKey(targetPath))
                {
                    string firstPath = Path.GetFullPath(targetPathTable[targetPath]!);
                    string secondPath = Path.GetFullPath(item.ItemSpec);

                    if (firstPath == secondPath)
                    {
                        Log.LogWarning(null, "WASM0003", "", "", 0, 0, 0, 0, $"Found identical vfs mappings for target path: {targetPath}, source file: {firstPath}. Ignoring.");
                        continue;
                    }

                    throw new LogAsErrorException($"Found more than one file mapping to the target VFS path: {targetPath}. Source files: {firstPath}, and {secondPath}");
                }

                targetPathTable[targetPath] = item.ItemSpec;

                var generatedFileName = $"{i++}_{Path.GetFileName(item.ItemSpec)}";
                var vfsPath = Path.Combine(supportFilesDir, generatedFileName);
                FileCopyChecked(item.ItemSpec, vfsPath, "FilesToIncludeInFileSystem");

                var vfsDict = loadingStage switch
                {
                    null => vfs,
                    "" => vfs,
                    "Core" => coreVfs,
                    _ => throw new LogAsErrorException($"The WasmFilesToIncludeInFileSystem '{item.ItemSpec}' has LoadingStage set to unsupported '{loadingStage}' (empty or 'Core' is currently supported).")
                };
                vfsDict[targetPath] = new()
                {
                    [$"supportFiles/{generatedFileName}"] = Utils.ComputeIntegrity(vfsPath)
                };
            }

            if (vfs.Count > 0)
                resources.vfs = vfs;

            if (coreVfs.Count > 0)
                resources.coreVfs = coreVfs;
        }

        if (!InvariantGlobalization)
        {
            bool loadRemote = RemoteSources?.Length > 0;
            foreach (var idfn in IcuDataFileNames)
            {
                if (!File.Exists(idfn))
                {
                    Log.LogError($"Expected the file defined as ICU resource: {idfn} to exist but it does not.");
                    return false;
                }

                resources.icu ??= new();
                resources.icu[Path.GetFileName(idfn)] = Utils.ComputeIntegrity(idfn);
            }
        }


        if (RemoteSources?.Length > 0)
        {
            resources.remoteSources = new();
            foreach (var source in RemoteSources)
                if (source != null && source.ItemSpec != null)
                    resources.remoteSources.Add(source.ItemSpec);
        }

        var extraConfiguration = new Dictionary<string, object?>();

        if (PThreadPoolInitialSize < -1)
        {
            throw new LogAsErrorException($"PThreadPoolInitialSize must be -1, 0 or positive, but got {PThreadPoolInitialSize}");
        }
        else if (PThreadPoolInitialSize > -1)
        {
            bootConfig.pthreadPoolInitialSize = PThreadPoolInitialSize;
        }

        if (PThreadPoolUnusedSize < -1)
        {
            throw new LogAsErrorException($"PThreadPoolUnusedSize must be -1, 0 or positive, but got {PThreadPoolUnusedSize}");
        }
        else if (PThreadPoolUnusedSize > -1)
        {
            bootConfig.pthreadPoolUnusedSize = PThreadPoolUnusedSize;
        }

        foreach (ITaskItem extra in ExtraConfig ?? Enumerable.Empty<ITaskItem>())
        {
            string name = extra.ItemSpec;
            if (!TryParseExtraConfigValue(extra, out object? valueObject))
                return false;

            if (string.Equals(name, nameof(BootJsonData.environmentVariables), StringComparison.OrdinalIgnoreCase))
            {
                bootConfig.environmentVariables ??= new();
                var envs = (JsonElement)valueObject!;
                foreach (var env in envs.EnumerateObject())
                {
                    bootConfig.environmentVariables[env.Name] = env.Value.GetString()!;
                }
            }
            else if (string.Equals(name, nameof(BootJsonData.diagnosticTracing), StringComparison.OrdinalIgnoreCase))
            {
                if (valueObject is bool boolValue || (valueObject is string stringValue && bool.TryParse(stringValue, out boolValue)))
                    bootConfig.diagnosticTracing = boolValue;
                else
                    throw new LogAsErrorException($"Unsupported value '{valueObject}' of type '{valueObject?.GetType()?.FullName}' for extra config 'diagnosticTracing'.");
            }
            else
            {
                extraConfiguration[name] = valueObject;
            }
        }

        Profilers ??= Array.Empty<string>();
        var browserProfiler = Profilers.FirstOrDefault(p => p.StartsWith("browser:"));
        if (browserProfiler != null)
        {
            bootConfig.environmentVariables ??= new();
            bootConfig.environmentVariables["DOTNET_WasmPerformanceInstrumentation"] = browserProfiler.Substring("browser:".Length);
        }

        if (RuntimeConfigJsonPath != null && File.Exists(RuntimeConfigJsonPath))
        {
            using var fs = File.OpenRead(RuntimeConfigJsonPath);
            var runtimeConfig = JsonSerializer.Deserialize<RuntimeConfigData>(fs, BootJsonBuilderHelper.JsonOptions);
            bootConfig.runtimeConfig = runtimeConfig;
        }

        foreach (ITaskItem env in EnvVariables ?? Enumerable.Empty<ITaskItem>())
        {
            bootConfig.environmentVariables ??= new();
            string name = env.ItemSpec;
            bootConfig.environmentVariables[name] = env.GetMetadata("Value");
        }

        if (extraConfiguration.Count > 0)
        {
            bootConfig.extensions = new()
            {
                ["extra"] = extraConfiguration
            };
        }

        using TempFileName tmpConfigPath = new();
        {
            helper.ComputeResourcesHash(bootConfig);
            helper.TransformResourcesToAssets(bootConfig);
            helper.WriteConfigToFile(bootConfig, tmpConfigPath.Path, Path.GetExtension(ConfigFileName));
        }

        string monoConfigPath = Path.Combine(runtimeAssetsPath, ConfigFileName);
        Utils.CopyIfDifferent(tmpConfigPath.Path, monoConfigPath, useHash: false);
        _fileWrites.Add(monoConfigPath);

        foreach (ITaskItem item in ExtraFilesToDeploy!)
        {
            string src = item.ItemSpec;
            string dst;

            string tgtPath = item.GetMetadata("TargetPath");
            if (!string.IsNullOrEmpty(tgtPath))
            {
                dst = Path.Combine(AppDir!, tgtPath);
                string? dstDir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir) && !Directory.Exists(dstDir))
                    Directory.CreateDirectory(dstDir!);
            }
            else
            {
                dst = Path.Combine(AppDir!, Path.GetFileName(src));
            }

            if (!FileCopyChecked(src, dst, "ExtraFilesToDeploy"))
                return false;
        }

        UpdateRuntimeConfigJson();
        return !Log.HasLoggedErrors;
    }

    private bool TryParseExtraConfigValue(ITaskItem extraItem, out object? valueObject)
    {
        valueObject = null;
        string? rawValue = extraItem.GetMetadata("Value");
        if (string.IsNullOrEmpty(rawValue))
            return true;

        if (TryConvert(rawValue, typeof(double), out valueObject) || TryConvert(rawValue, typeof(bool), out valueObject))
            return true;

        // Try parsing as a quoted string
        if (rawValue!.Length > 1 && rawValue![0] == '"' && rawValue![rawValue!.Length - 1] == '"')
        {
            valueObject = rawValue!.Substring(1, rawValue!.Length - 2);
            return true;
        }

        // try parsing as json
        try
        {
            JsonDocument jdoc = JsonDocument.Parse(rawValue);
            valueObject = jdoc.RootElement;
            return true;
        }
        catch (JsonException je)
        {
            Log.LogError($"ExtraConfig: {extraItem.ItemSpec} with Value={rawValue} cannot be parsed as a number, boolean, string, or json object/array: {je.Message}");
            return false;
        }
    }

    private static bool TryConvert(string str, Type type, out object? value)
    {
        value = null;
        try
        {
            value = Convert.ChangeType(str, type);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }
}
