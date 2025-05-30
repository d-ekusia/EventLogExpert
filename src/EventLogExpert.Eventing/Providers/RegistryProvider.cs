﻿// // Copyright (c) Microsoft Corporation.
// // Licensed under the MIT License.

using EventLogExpert.Eventing.Helpers;
using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace EventLogExpert.Eventing.Providers;

public partial class RegistryProvider(string? computerName, ITraceLogger? logger = null)
{
    private readonly string? _computerName = computerName;
    private readonly ITraceLogger? _logger = logger;

    /// <summary>sounds Returns the file paths for the message files for this provider.</summary>
    public IEnumerable<string> GetMessageFilesForLegacyProvider(string providerName)
    {
        _logger?.Trace($"GetLegacyProviderFiles called for provider {providerName} on computer {_computerName}");

        var hklm = string.IsNullOrEmpty(_computerName)
            ? Registry.LocalMachine
            : RegistryKey.OpenRemoteBaseKey(RegistryHive.LocalMachine, _computerName);

        var eventLogKey = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\EventLog") ??
            throw new OpenEventLogRegistryKeyFailedException(_computerName ?? string.Empty);

        foreach (var logSubKeyName in eventLogKey.GetSubKeyNames())
        {
            // Skip Security and State since it requires elevation
            if (logSubKeyName is "Security" or "State")
            {
                continue;
            }

            var logSubKey = eventLogKey.OpenSubKey(logSubKeyName);
            var providerSubKey = logSubKey?.OpenSubKey(providerName);

            if (providerSubKey?.GetValue("EventMessageFile") is not string eventMessageFilePath)
            {
                continue;
            }

            _logger?.Trace($"Found message file for legacy provider {providerName} in subkey {providerSubKey.Name}");

            // Filter by extension. The FltMgr provider puts a .sys file in the EventMessageFile value,
            // and trying to load that causes an access violation.
            var supportedExtensions = new[] { ".dll", ".exe" };

            var messageFiles = eventMessageFilePath
                .Split(';')
                .Where(path => supportedExtensions.Contains(Path.GetExtension(path).ToLower()))
                .ToList();

            IEnumerable<string> files;

            if (providerSubKey.GetValue("CategoryMessageFile") is string categoryMessageFilePath)
            {
                var fileList = new List<string> { categoryMessageFilePath };
                fileList.AddRange(messageFiles.Where(f => f != categoryMessageFilePath));
                files = fileList;
            }
            else
            {
                files = messageFiles;
            }

            // Now we have all our paths, but they are not expanded yet, so expand them
            files = GetExpandedFilePaths(files).ToList();

            hklm.Close();
            return files;
        }

        hklm.Close();

        return [];
    }

    [GeneratedRegex("^[A-Z]:")]
    private static partial Regex ConvertRootPath();

    private IEnumerable<string> GetExpandedFilePaths(IEnumerable<string> paths)
    {
        if (string.IsNullOrEmpty(_computerName))
        {
            // For local computer, do it the easy way
            return paths.Select(Environment.ExpandEnvironmentVariables);
        }

        // For remote computer, get SystemRoot from the registry
        // TODO: Support variables other than SystemRoot?
        var systemRoot = GetSystemRoot() ??
            throw new ExpandFilePathsFailedException(
                $"Could not get SystemRoot from remote registry: {_computerName}");

        paths = paths.Select(p =>
        {
            // Expand the variable
            var newPath = p.ReplaceCaseInsensitiveFind("%SystemRoot%", systemRoot);

            // Now replace any drive root references with \\computername\drive$
            var match = ConvertRootPath().Match(newPath);

            if (match.Success)
            {
                newPath = $@"\\{_computerName}\{match.Value[0]}${newPath[2..]}";
            }

            return newPath;
        });

        return paths;
    }

    private string? GetSystemRoot()
    {
        var hklm = string.IsNullOrEmpty(_computerName)
            ? Registry.LocalMachine
            : RegistryKey.OpenRemoteBaseKey(RegistryHive.LocalMachine, _computerName);

        var currentVersion = hklm.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion");
        var systemRoot = currentVersion?.GetValue("SystemRoot") as string;

        return systemRoot;
    }

    private class ExpandFilePathsFailedException(string msg) : Exception(msg) {}

    private class OpenEventLogRegistryKeyFailedException(string msg) : Exception(msg) {}
}
