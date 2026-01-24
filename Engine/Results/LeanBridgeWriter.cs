/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/

using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace QuantConnect.Lean.Engine.Results
{
    public class LeanBridgeWriter
    {
        private readonly string _outputDir;
        private readonly JsonSerializerSettings _settings;

        public LeanBridgeWriter(string outputDir)
        {
            _outputDir = outputDir;
            _settings = new JsonSerializerSettings
            {
                ContractResolver = new DefaultContractResolver
                {
                    NamingStrategy = new CamelCaseNamingStrategy
                    {
                        ProcessDictionaryKeys = false,
                        OverrideSpecifiedNames = true
                    }
                }
            };
            Directory.CreateDirectory(_outputDir);
        }

        public void WriteJsonAtomic(string filename, object payload)
        {
            Directory.CreateDirectory(_outputDir);
            var path = Path.Combine(_outputDir, filename);
            var tmp = path + ".tmp";
            var json = JsonConvert.SerializeObject(payload, Formatting.Indented, _settings);
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, true);
        }

        public void AppendJsonLine(string filename, object payload)
        {
            Directory.CreateDirectory(_outputDir);
            var path = Path.Combine(_outputDir, filename);
            var json = JsonConvert.SerializeObject(payload, _settings);
            File.AppendAllText(path, json + "\n");
        }
    }
}
