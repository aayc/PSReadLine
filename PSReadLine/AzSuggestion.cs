// Copyright (c) Microsoft Corporation.
// Licensed under the 2-Clause BSD License.

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Microsoft.PowerShell
{
    public partial class PSConsoleReadLine
    {
        private const char commandSplitTokens = '|';
        private const int nHistoryLines = 2;
        private const string serviceUri = "https://localhost:44386/api/v1/prediction";
        private Regex azCmdletRegex = new Regex(@"\b\w+-Az\w+\b", RegexOptions.IgnoreCase);
        private HttpClient client = new HttpClient();
        private List<string> suggestions = new List<string>();
        private List<string> commands = new List<string>();
        private HashSet<string> commandSet = new HashSet<string>();
        private List<Dictionary<string, string>> logs = new List<Dictionary<string, string>>();

        bool waitForPredictions = true;
        bool waitForCommands = true;

        public string GetAzSuggestion(string line)
        {
            var segments = line.Split(commandSplitTokens);
            var lastSegment = segments.Last();
            var leadingSpaces = lastSegment.Length - lastSegment.TrimStart().Length;
            var text = lastSegment.TrimStart();
            string suggestion = null;
            if (!waitForPredictions)
            {
                suggestion = suggestions.FirstOrDefault(option => option.StartsWith(text, Options.HistoryStringComparison));
            }

            if (!waitForCommands && suggestion == null)
            {
                suggestion = commands
                    .FirstOrDefault(command => command.StartsWith(text, Options.HistoryStringComparison));
            }

            if (suggestion != null)
            {
                segments[segments.Length - 1] = new string(' ', leadingSpaces) + suggestion;
                return String.Join("" + commandSplitTokens, segments);
            }
            else
            {
                return suggestion;
            }
        }

        public void LogAzSuggestionTelemetry(string submitted)
        {
            var log = new Dictionary<string, string>();
            var usedHistory = false;
            var usedSuggestion = false;

            // Take just the first command
            submitted = azCmdletRegex.Match(submitted).Value;
            log["submitted"] = submitted;
            log["history"] = ProcessHistory();
            log["suggestions"] = String.Join(Environment.NewLine, suggestions);

            if (suggestions.Any(suggestion => submitted.Equals(suggestion, Options.HistoryStringComparison)))
            {
                usedSuggestion = true;
            }
            else
            {
                for (int index = _history.Count - 1; index > 0; index--)
                {
                    var line = _history[index].CommandLine;
                    if (line.Equals(submitted, Options.HistoryStringComparison))
                    {
                        usedHistory = true;
                        break;
                    }
                }
            }
            log["usedSuggestion"] = usedSuggestion.ToString();
            log["usedHistory"] = usedHistory.ToString();
            logs.Add(log);

            if (logs.Count % 10 == 0)
            {
                Debug.WriteLine("TODO: aggregate and write logs");
            }
        }

        public void RequestPredictions()
        {
            if (waitForCommands) return;

            waitForPredictions = true;
            suggestions.Clear();
            var historySnippet = ProcessHistory();
            var requestBody = JsonConvert.SerializeObject(new Dictionary<string, dynamic> {
                { "history", historySnippet },
                { "clientType", "AzurePowerShell" },
                { "context", new Dictionary<string, string>{
                    { "CorrelationId", "00000000-0000-0000-0000-000000000000" },
                    { "SessionId", "00000000-0000-0000-0000-000000000000" },
                    { "SubscriptionId", "00000000-0000-0000-0000-000000000000" },
                    { "VersionNumber", "1.0" }
                }}
            });
            client
                .PostAsync(serviceUri, new StringContent(requestBody, Encoding.UTF8, "application/json"))
                .ContinueWith(async (requestTask) =>
                {
                    var reply = await requestTask.Result.Content.ReadAsStringAsync();
                    suggestions = JsonConvert.DeserializeObject<List<string>>(reply);
                    waitForPredictions = false;
                });
        }

        public void RequestCommands()
        {
            waitForCommands = true;
            client
                .GetAsync(serviceUri)
                .ContinueWith(async (requestTask) =>
                {
                    var reply = await requestTask.Result.Content.ReadAsStringAsync();
                    commands = JsonConvert.DeserializeObject<List<string>>(reply);
                    commandSet = new HashSet<string>(commands.Select(x => x.ToLower()));
                    waitForCommands = false;
                    RequestPredictions();
                });
        }

        private string ProcessHistory()
        {
            var previousLines = new List<string>(nHistoryLines);
            for (int i = _history.Count - nHistoryLines; i < _history.Count; i++)
            {
                // If no history available, use start token
                if (i < 0)
                {
                    previousLines.Add("start_of_snippet");
                    continue;
                }

                // If history is not related, use start token
                var azCommand = azCmdletRegex.Match(_history[i].CommandLine);
                if (!commandSet.Contains(azCommand.Value.ToLower()))
                {
                    // disregard all previous history and add start token
                    previousLines = previousLines.Select(_ => "start_of_snippet").ToList();
                    previousLines.Add("start_of_snippet");
                    continue;
                }

                // If history is related, normalize parameters
                var line = _history[i].CommandLine.Substring(azCommand.Index);
                string[] chunks =line.Split(' ');
                List<List<string>> args = new List<List<string>>();
                bool isOption = false;
                for (int j = 1; j < chunks.Length; j++)
                {
                    if (chunks[j].StartsWith("-"))
                    {
                        isOption = true;
                        List<string> option = new List<string>();
                        option.Add(chunks[j]);
                        args.Add(option);
                    }
                    else if (isOption && chunks[j] != "")
                    {
                        args.Last().Add("***");
                        isOption = false;
                    }
                }
                string processedLine = chunks[0] + " " + String.Join(" ", args
                    .OrderBy(parameter => parameter.First())
                    .Select(parameter => String.Join(" ", parameter)));

                previousLines.Add(processedLine);
            }
            return String.Join("\n", previousLines);
        }

        private string GetTmpFilePath(string fileName)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft",
                    "Windows",
                    "PowerShell",
                    "PSReadLine",
                    fileName);
            }
            else
            {
                // PSReadLine can't use Utils.CorePSPlatform (6.0+ only), so do the equivalent:
                var path = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

                if (!String.IsNullOrEmpty(path))
                {
                    return System.IO.Path.Combine(path, "powershell", "PSReadLine", fileName);
                }
                else
                {
                    // Telemetry is data, so it goes into .local/share/powershell folder
                    var home = Environment.GetEnvironmentVariable("HOME");

                    if (!String.IsNullOrEmpty(home))
                    {
                        return System.IO.Path.Combine(home, ".local", "share", "powershell", "PSReadLine", fileName);
                    }
                    else
                    {
                        // No HOME, then don't save anything
                        return "/dev/null";
                    }
                }
            }
        }
    }
}
