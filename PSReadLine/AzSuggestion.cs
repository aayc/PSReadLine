// Copyright (c) Microsoft Corporation.
// Licensed under the 2-Clause BSD License.

using Microsoft.PowerShell;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Globalization;
using Microsoft.PowerShell.PSReadLine;

namespace Microsoft.PowerShell
{
    public partial class PSConsoleReadLine
    {
        private readonly int nHistoryLines = 2;
        private readonly string serviceUri = "";
        private readonly string[] noise_commands = { "-Verbose", "-ErrorAction", "-Debug", "-ErrorVariable", "-OutVariable", "-OutBuffer" };
        private readonly Regex lineSplitRegex = new Regex(@"([|=])|(&&)");
        private readonly Regex azCmdletRegex = new Regex(@"\b\w+-Az\w+\b", RegexOptions.IgnoreCase);
        private readonly HttpClient client = new HttpClient();
        private int nKeystrokes = 0; // for telemetry purposes
        private int nSuggestionPartsAccepted = 0;
        private int nHistoryPartsAccepted = 0;
        private Guid sessionGuid;
        private AzPredictor predictions = new AzPredictor(new List<string>());
        private AzPredictor commands = new AzPredictor(new List<string>());
        private HashSet<string> commandSet = new HashSet<string>();
        private TelemetryClient telemetryClient;

        bool waitForPredictions = true;
        bool waitForCommands = true;

        public void InitializeAzSuggestionExtension()
        {
            TelemetryConfiguration configuration = TelemetryConfiguration.CreateDefault();
            configuration.InstrumentationKey = "";
            telemetryClient = new TelemetryClient(configuration);
            sessionGuid = Guid.NewGuid();
            RequestCommands();

            // One time install event logging
            var flagFileName = GetTmpFilePath("AzPredictIsInstalled.txt");
            if (!File.Exists(flagFileName))
            {
                File.Create(flagFileName);
                telemetryClient.TrackEvent("install");
                telemetryClient.Flush();
            }
        }

        public string GetAzSuggestion(string line)
        {
            nKeystrokes++;

            var segments = lineSplitRegex.Split(line);
            var lastSegment = segments.Last();
            var leadingSpaces = lastSegment.Length - lastSegment.TrimStart().Length;
            var text = lastSegment.TrimStart();
            string suggestion = null;
            if (!waitForPredictions)
            {
                suggestion = predictions.Query(text)?.Item1;
            }

            if (!waitForCommands && suggestion == null)
            {
                suggestion = commands.Query(text)?.Item1;
            }

            if (suggestion != null)
            {
                segments[segments.Length - 1] = new string(' ', leadingSpaces) + suggestion;
                return String.Join("", segments);
            }
            else
            {
                return suggestion;
            }
        }

        public void LogAzSuggestionTelemetry(string line)
        {
            var log = new Dictionary<string, string>();
            var cmd = azCmdletRegex.Match(line).Value;
            if (!commandSet.Contains(cmd.ToLower()))
            {
                return;
            }

            var query = predictions.Query(line);
            var suggestionIndex = query == null ? -1 : query.Item2;
            var azCommand = azCmdletRegex.Match(line);
            log["line"] = ProcessLine(line.Substring(azCommand.Index));
            log["keystrokes"] = nKeystrokes.ToString();
            log["num_history_accepted"] = nHistoryPartsAccepted.ToString();
            log["num_suggestions_part_accepted"] = nSuggestionPartsAccepted.ToString();
            log["line_length"] = line.Length.ToString();
            log["sessionId"] = sessionGuid.ToString();
            log["suggestion_index"] = suggestionIndex.ToString();

            Debug.WriteLine("LOG:");
            foreach (string key in log.Keys)
            {
                Debug.WriteLine(String.Format("{0}: {1}", key, log[key]));
            }

            telemetryClient.TrackEvent("submission", log);
            Task.Run(() => telemetryClient.Flush());
        }

        public void LogAzAcceptSuggestionPart(string suggestionPart)
        {
            var query = predictions.Query(suggestionPart);
            var suggestionIndex = query == null ? -1 : query.Item2;
            if (suggestionIndex != -1)
            {
                nSuggestionPartsAccepted++;
            }
            else
            {
                nHistoryPartsAccepted++;
            }
        }

        public void RequestPredictions()
        {
            ClearKeystrokeMetrics();
            if (waitForCommands) return;

            waitForPredictions = true;
            predictions = new AzPredictor(new List<string>());
            var historySnippet = ProcessHistory();
            Debug.WriteLine("History: " + historySnippet);
            var requestBody = JsonConvert.SerializeObject(new Dictionary<string, dynamic> {
                { "history", historySnippet },
                { "clientType", "AzurePowerShell" },
                { "context", new Dictionary<string, string>{
                    { "CorrelationId", "00000000-0000-0000-0000-000000000000" }, // Will use this when AzRMCmdlet
                    { "SessionId", "00000000-0000-0000-0000-000000000000" },
                    { "SubscriptionId", "00000000-0000-0000-0000-000000000000" },
                    { "VersionNumber", "1.0" }
                }}
            });
            Debug.WriteLine("Request body: " + requestBody);
            client
                .PostAsync(serviceUri + "/predictions", new StringContent(requestBody, Encoding.UTF8, "application/json"))
                .ContinueWith(async (requestTask) =>
                {
                    var reply = await requestTask.Result.Content.ReadAsStringAsync();
                    var suggestionsList = JsonConvert.DeserializeObject<List<string>>(reply);
                    Debug.WriteLine("suggestions: " + String.Join(";", suggestionsList));
                    predictions = new AzPredictor(suggestionsList);
                    waitForPredictions = false;
                });
        }

        public void RequestCommands()
        {
            waitForCommands = true;
            client
                .GetAsync(serviceUri + "/commands")
                .ContinueWith(async (requestTask) =>
                {
                    var reply = await requestTask.Result.Content.ReadAsStringAsync();
                    var commands_reply = JsonConvert.DeserializeObject<List<string>>(reply);
                    commands = new AzPredictor(commands_reply);
                    commandSet = new HashSet<string>(commands_reply.Select(x => x.Split(' ')[0].ToLower()));
                    waitForCommands = false;
                    RequestPredictions();
                });
        }

        // Splits console line into parts: a part is either a command, parameter name, or parameter name with value.
        public static string[] SplitConsoleLine(string s)
        {
            var parts = s.Split(' ');
            var lineFeed = new List<string>() { parts[0] };
            var count = 0;
            for (int i = 1; i < parts.Length; i++)
            {
                if (!parts[i].StartsWith("-") && !parts[i - 1].StartsWith("-"))
                {
                    lineFeed[count] += " " + parts[i];
                }
                else
                {
                    lineFeed.Add(parts[i]);
                    count++;
                }
            }
            return lineFeed.ToArray();
        }

        public static string JoinConsoleLine(string command, List<List<string>> parameters)
        {
            var parameterPart = String.Join(" ", parameters
                    .OrderBy(parameter => parameter.First())
                    .Select(parameter => String.Join(" ", parameter)));
            var lineParts = new List<string> { command, parameterPart };
            return String.Join(" ", lineParts).Trim();
        }

        public void ClearKeystrokeMetrics()
        {
            nKeystrokes = 0;
            nHistoryPartsAccepted = 0;
            nSuggestionPartsAccepted = 0;
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
                previousLines.Add(ProcessLine(line));
            }
            return String.Join("\n", previousLines);
        }

        private string ProcessLine (string line)
        {
            string[] chunks = line.Split(' ');
            List<List<string>> args = new List<List<string>>();
            bool isOption = false;
            for (int j = 1; j < chunks.Length; j++)
            {
                if (chunks[j].StartsWith("-") && !Enumerable.Contains(noise_commands, chunks[j]))
                {
                    isOption = true;
                    args.Add(new List<string>() { chunks[j] });
                }
                else if (isOption && chunks[j] != "")
                {
                    isOption = false;
                    args.Last().Add("***");
                }
            }

            return JoinConsoleLine(chunks[0], args).Trim();
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
