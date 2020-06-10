using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;

namespace Microsoft.PowerShell
{
    public partial class PSConsoleReadLine
    {
        const char commandSplitTokens = '|';
        const string serviceUri = "http://localhost:3000/azpredict";
        HttpClient client = new HttpClient();
        List<string> suggestions = new List<string>();
        List<string> commands = new List<string>();
        HashSet<string> commandSet = new HashSet<string>();
        List<Dictionary<string, string>> logs = new List<Dictionary<string, string>>();
        
        bool waitForPredictions = true;
        bool waitForCommands = true;

        string ProcessHistory()
        {
            int nLookBack = 2;
            List<string> previousLines = new List<string>(nLookBack);
            for (int i = _history.Count - nLookBack; i < _history.Count; i++)
            {
                // If no history available, use start token
                if (i < 0)
                {
                    previousLines.Add("start_of_snippet");
                    continue;
                }

                // If history is not related, use start token
                if (!commandSet.Contains(_history[i].CommandLine.Split(' ')[0]))
                {
                    // disregard all previous history and add start token
                    previousLines = previousLines.Select(_ => "start_of_snippet").ToList();
                    previousLines.Add("start_of_snippet");
                    continue;
                }

                // If history is related, normalize parameters
                string[] chunks = _history[i].CommandLine.Split(' ');
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

        string GetTelemetryFilePath()
        {
           var telemetryFileName = "suggestion_telemetry.txt";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft",
                    "Windows",
                    "PowerShell",
                    "PSReadLine",
                    telemetryFileName);
            }
            else
            {
                // PSReadLine can't use Utils.CorePSPlatform (6.0+ only), so do the equivalent:
                string telemetryPath = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

                if (!String.IsNullOrEmpty(telemetryPath))
                {
                    return System.IO.Path.Combine(
                        telemetryPath,
                        "powershell",
                        "PSReadLine",
                        telemetryFileName);
                }
                else
                {
                    // Telemetry is data, so it goes into .local/share/powershell folder
                    var home = Environment.GetEnvironmentVariable("HOME");

                    if (!String.IsNullOrEmpty(home))
                    {
                        return System.IO.Path.Combine(
                            home,
                            ".local",
                            "share",
                            "powershell",
                            "PSReadLine",
                            telemetryFileName);
                    }
                    else
                    {
                        // No HOME, then don't save anything
                        return "/dev/null";
                    }
                }
            }
        }

        void RequestPredictions()
        {
            if (waitForCommands) return;

            waitForPredictions = true;
            suggestions.Clear();
            string historySnippet = ProcessHistory();
            string requestBody = JsonConvert.SerializeObject(new Dictionary<string, string> { { "history", historySnippet } });
            client
                .PostAsync(serviceUri, new StringContent(requestBody, Encoding.UTF8, "application/json"))
                .ContinueWith(async (requestTask) => {
                    string reply = await requestTask.Result.Content.ReadAsStringAsync();
                    suggestions = JsonConvert.DeserializeObject<List<string>>(reply);
                    waitForPredictions = false;
                });
        }

        void RequestCommands()
        {
            waitForCommands = true;
            client
                .GetAsync(serviceUri)
                .ContinueWith(async (requestTask) => {
                    string reply = await requestTask.Result.Content.ReadAsStringAsync();
                    commands = JsonConvert.DeserializeObject<List<string>>(reply);
                    commandSet = new HashSet<string>(commands);
                    waitForCommands = false;
                    RequestPredictions();
                });
        }

        void LogAzSuggestionTelemetry(string submitted)
        {
            var log = new Dictionary<string, string>();
            bool usedHistory = false;
            bool usedSuggestion = false;

            // Take just the first command
            submitted = submitted.Trim().Split(' ')[0];
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
                Debug.WriteLine("TODO: write logs");
            }
        }

        string GetAzSuggestion(string line)
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
    }
}
