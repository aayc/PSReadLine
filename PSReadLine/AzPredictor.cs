using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.PowerShell.PSReadLine
{
    public class AzPredictor
    {
        private List<Prediction> predictions;
        public AzPredictor(List<string> modelPredictions)
        {
            predictions = new List<Prediction>();
            foreach (var predictionText in modelPredictions)
            {
                var predictionParts = PSConsoleReadLine.SplitConsoleLine(predictionText);

                var existingPrediction = predictions.FirstOrDefault(p => p.command == predictionParts.First());
                if (existingPrediction != null)
                {
                    existingPrediction.bags.Add(new ParameterBag(predictionParts));
                } 
                else
                {
                    predictions.Add(new Prediction(predictionParts.First(), new ParameterBag(predictionParts)));
                }
            }
        }

        public Tuple<string, int> Query(string input)
        {
            var inputParts = PSConsoleReadLine.SplitConsoleLine(input);
            var commandIsFinished = inputParts.Length > 1;
            var predictionIndex = predictions.FindIndex(pred => (pred.command + " ").StartsWith(inputParts[0] + (commandIsFinished ? " " : ""), StringComparison.OrdinalIgnoreCase));
            if (predictionIndex == -1)
            {
                return null;
            }
            else
            {
                var prediction = predictions[predictionIndex];
                var builder = new StringBuilder(prediction.command);
                var inputBag = new ParameterBag(inputParts);
                var usedParams = new HashSet<int>();
                var usedArgs = new HashSet<int>();
                var i = 0;

                while (i < prediction.bags.Count)
                {
                    var canUseBag = true;
                    builder = new StringBuilder(prediction.command);
                    usedParams.Clear();
                    usedArgs.Clear();
                    for (var j = 0; j < inputBag.parameters.Count; j++)
                    {
                        var match = -1;
                        for (var k = 0; k < prediction.bags[i].parameters.Count; k++)
                        {
                            var isPrefixed = prediction.bags[i].parameters[k].Item1.StartsWith(inputBag.parameters[j].Item1, StringComparison.OrdinalIgnoreCase);
                            var hasNotBeenUsed = !usedParams.Contains(k);
                            if (isPrefixed && hasNotBeenUsed)
                            {
                                match = k;
                                break;
                            }
                        }

                        if (match == -1)
                        {
                            canUseBag = false;
                            break;
                        }
                        else
                        {
                            usedParams.Add(match);
                            builder.Append(" ");
                            builder.Append(prediction.bags[i].parameters[match].Item1);
                            if (inputBag.parameters[j].Item2 != null)
                            {
                                builder.Append(" ");
                                builder.Append(inputBag.parameters[j].Item2);
                                usedArgs.Add(match);
                            }
                            else
                            {
                                if (prediction.bags[i].parameters[match].Item2.Length > 0)
                                {
                                    builder.Append(" ");
                                }
                                builder.Append(prediction.bags[i].parameters[match].Item2);
                                usedArgs.Add(match);
                            }
                        }
                    }

                    if (canUseBag)
                    {
                        break;
                    }
                    i++;
                }

                if (i < prediction.bags.Count)
                {
                    for (var j = 0; j < prediction.bags[i].parameters.Count; j++)
                    {
                        if (!usedParams.Contains(j))
                        {
                            builder.Append(" "); 
                            builder.Append(prediction.bags[i].parameters[j].Item1);
                        }

                        if (!usedArgs.Contains(j))
                        {
                            builder.Append(" "); 
                            builder.Append(prediction.bags[i].parameters[j].Item2);
                        }
                    }

                    var result = builder.ToString();

                    if (result.Length <= input.Length) {
                        return null;
                    }
                    else
                    {
                        return new Tuple<string, int>(builder.ToString(), predictionIndex);
                    }
                }
                else
                {
                    return null;
                }
            }
        }

        private class Prediction
        {
            public string command;
            public List<ParameterBag> bags;
            public Prediction(string command, ParameterBag bag)
            {
                this.command = command;
                bags = new List<ParameterBag>();
                bags.Add(bag);
            }
        }

        private class ParameterBag
        {
            public List<Tuple<string, string>> parameters;
            public ParameterBag(string[] parts)
            {
                parameters = new List<Tuple<string, string>>();
                string param = null;
                string arg = null;
                for (var j = 1; j < parts.Length; j++)
                {
                    if (parts[j].StartsWith("-"))
                    {
                        if (param != null)
                        {
                            parameters.Add(new Tuple<string, string>(param, arg));
                        }
                        param = parts[j];
                        arg = null;
                    }
                    else 
                    {
                        arg = parts[j];
                    }
                }

                if (param != null)
                {
                    parameters.Add(new Tuple<string, string>(param, arg));

                }
            }
        }
    }
}
