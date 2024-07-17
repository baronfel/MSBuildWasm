﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Wasmtime;


namespace MSBuildWasm
{
    public abstract class WasmTask : Microsoft.Build.Utilities.Task, IWasmTask
    {
        const string ExecuteFunctionName = "Execute";
        public string WasmFilePath { get; set; }
        public ITaskItem[] Directories { get; set; } = [];
        public bool InheritEnv { get; set; } = false;
        public string Environment { get; set; } = null;

        // Reflection in WasmTaskFactory will add properties to a subclass.

        private static readonly HashSet<string> s_blacklistedProperties =
            [nameof(WasmFilePath), nameof(InheritEnv), nameof(ExecuteFunctionName),
            nameof(Directories), nameof(InheritEnv), nameof(Environment)];
        private DirectoryInfo _sharedTmpDir;
        private DirectoryInfo _hostTmpDir;
        private string _inputPath;
        private string _outputPath;

        public WasmTask()
        {
        }

        public override bool Execute()
        {
            CreateTmpDirs();
            CopyAllTaskItemPropertiesToTmp();
            CreateTaskIO();
            ExecuteWasm();
            return !Log.HasLoggedErrors;
        }

        private WasiConfiguration GetWasiConfig()
        {
            var wasiConfig = new WasiConfiguration();
            // setup env
            if (InheritEnv)
            {
                wasiConfig = wasiConfig.WithInheritedEnvironment();
            }
            wasiConfig = wasiConfig.WithStandardOutput(_outputPath).WithStandardInput(_inputPath);
            wasiConfig = wasiConfig.WithPreopenedDirectory(_sharedTmpDir.FullName, ".");
            foreach (ITaskItem dir in Directories)
            {
                wasiConfig = wasiConfig.WithPreopenedDirectory(dir.ItemSpec, dir.ItemSpec);
            }

            return wasiConfig;

        }

        private Instance SetupWasmtimeInstance(Engine engine, Store store)
        {
            store.SetWasiConfiguration(GetWasiConfig());

            using var module = Wasmtime.Module.FromFile(engine, WasmFilePath);
            using var linker = new Linker(engine);
            linker.DefineWasi();
            LinkLogFunctions(linker, store);
            LinkTaskInfo(linker, store);

            return linker.Instantiate(store, module);

        }
        private void ExecuteWasm()
        {
            using var engine = new Engine();
            using var store = new Store(engine);
            Instance instance = SetupWasmtimeInstance(engine, store);

            Func<int> execute = instance.GetFunction<int>(ExecuteFunctionName);

            try
            {
                if (execute == null)
                {
                    throw new WasmtimeException($"Function {ExecuteFunctionName} not found in WebAssembly module.");
                }

                int taskResult = execute.Invoke();
                store.Dispose(); // store holds onto the output json file
                // read output file
                ExtractOutputs();
                if (taskResult != 0) // 0 success inside otherwise error
                {
                    Log.LogError($"Task failed.");
                }
            }
            catch (WasmtimeException ex)
            {
                Log.LogError($"Error executing WebAssembly module: {ex.Message}");
            }
            catch (IOException ex)
            {
                Log.LogError($"Error reading output file: {ex.Message}");

            }
            finally { Cleanup(); }

        }

        private void CreateTaskIO()
        {
            _inputPath = Path.Combine(_hostTmpDir.FullName, "input.json");
            _outputPath = Path.Combine(_hostTmpDir.FullName, "output.json");
            File.WriteAllText(_inputPath, CreateTaskInputJSON());
            Log.LogMessage(MessageImportance.Low, $"Created input file: {_inputPath}");

        }
        private void CreateTmpDirs()
        {
            _sharedTmpDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())); // trycatch?
            _hostTmpDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())); // trycatch?
            Log.LogMessage(MessageImportance.Low, $"Created shared temporary directories: {_sharedTmpDir} and {_hostTmpDir}");
        }

        /// <summary>
        /// Remove temporary directories.
        /// </summary>
        private void Cleanup()
        {
            DeleteTemporaryDirectory(_sharedTmpDir);
            DeleteTemporaryDirectory(_hostTmpDir);
        }

        private void DeleteTemporaryDirectory(DirectoryInfo directory)
        {
            if (directory != null)
            {
                try
                {
                    Directory.Delete(directory.FullName, true);
                    Log.LogMessage(MessageImportance.Low, $"Removed temporary directory: {directory}");
                }
                catch (Exception ex)
                {
                    Log.LogMessage(MessageImportance.High, $"Failed to remove temporary directory: {directory}. Exception: {ex.Message}");
                }
            }
        }


        private void CopyTaskItemToTmpDir(ITaskItem taskItem)
        {
            // Get the ItemSpec (the path)
            string sourcePath = taskItem.ItemSpec;
            // Create the destination path in the _tmpPath directory
            string destinationPath = Path.Combine(_sharedTmpDir.FullName, Path.GetFileName(sourcePath)); //sus

            // Ensure the directory exists
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));

            // Copy the file to the new location
            File.Copy(sourcePath, destinationPath, overwrite: true);

            Log.LogMessage(MessageImportance.Low, $"Copied {sourcePath} to {destinationPath}");
        }

        /// <summary>
        /// Use reflection to figure out what ITaskItem and ITaskItem[] typed properties are here in the class.
        /// Copy their content to the tmp directory for the sandbox to use.
        /// TODO: check size to not copy large amounts of data
        /// </summary>
        /// <remarks>If wasmtime implemented per-file access this effort could be saved.</remarks>
        /// TODO: save effort copying if a file's Directory is preopened
        private void CopyAllTaskItemPropertiesToTmp()
        {
            PropertyInfo[] properties = GetType().GetProperties().Where(p =>
                (typeof(ITaskItem).IsAssignableFrom(p.PropertyType) || typeof(ITaskItem[]).IsAssignableFrom(p.PropertyType))
                // filter out properties that are not ITaskItem or ITaskItem[] or are in the blacklist use LINQ
                && !s_blacklistedProperties.Contains(p.Name)
            ).ToArray();

            foreach (PropertyInfo property in properties)
            {
                CopyPropertyToTmp(property);
            }
        }

        private void CopyPropertyToTmp(PropertyInfo property)
        {
            object value = property.GetValue(this);

            if (value is ITaskItem taskItem && taskItem != null)
            {
                CopyTaskItemToTmpDir(taskItem);
            }
            else if (value is ITaskItem[] taskItems && taskItems != null)
            {
                foreach (ITaskItem taskItem_ in taskItems)
                {
                    CopyTaskItemToTmpDir(taskItem_);
                }
            }
        }
        private static Dictionary<string, string> SerializeITaskItem(ITaskItem item)
        {
            var taskItemDict = new Dictionary<string, string>
            {
                ["ItemSpec"] = item.ItemSpec
            };
            foreach (object metadata in item.MetadataNames)
            {
                taskItemDict[metadata.ToString()] = item.GetMetadata(metadata.ToString());
            }
            return taskItemDict;
        }
        private static Dictionary<string, string>[] SerializeITaskItems(ITaskItem[] items)
        {
            return items.Select(SerializeITaskItem).ToArray();
        }

        private static bool IsSupportedType(Type type)
        {
            return type == typeof(string) || type == typeof(bool) || type == typeof(ITaskItem) || type == typeof(ITaskItem[]) || type == typeof(string[]) || type == typeof(bool[]);
        }

        /// <summary>
        /// Use reflection to gather properties of this class, except for the ones on the blacklist, and serialize them to a json.
        /// </summary>
        /// <returns>string of a json</returns>
        private string SerializeProperties()
        {
            var propertiesToSerialize = GetType()
                                        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                        .Where(p => !s_blacklistedProperties.Contains(p.Name) && IsSupportedType(p.PropertyType))
                                        .ToDictionary(p => p.Name, p =>
                                        {
                                            object value = p.GetValue(this);
                                            if (value is ITaskItem taskItem)
                                            {
                                                return SerializeITaskItem(taskItem);
                                            }
                                            else if (value is ITaskItem[] taskItemList)
                                            {
                                                return SerializeITaskItems(taskItemList);
                                            }
                                            return value;
                                        });

            return JsonSerializer.Serialize(propertiesToSerialize);
        }
        // this is also not so simple D: 
        private string SerializeDirectories()
        {
            // TODO: probably wrong
            return JsonSerializer.Serialize(Directories.Select(d => d.ItemSpec).ToArray());
        }
        private string CreateTaskInputJSON()
        {
            var sb = new StringBuilder();
            sb.Append("{\"Properties\":");
            sb.Append(SerializeProperties());
            sb.Append(",\"Directories\":");
            sb.Append(SerializeDirectories());
            sb.Append('}');

            return sb.ToString();

        }


        /// <summary>
        /// 1. Read the output file
        /// 2. copy outputs that are files
        /// </summary>
        /// <returns></returns>
        private bool ExtractOutputs()
        {
            if (File.Exists(_outputPath))
            {
                string taskOutput = File.ReadAllText(_outputPath);
                // TODO: figure out where to copy the output files
                ReflectOutputJsonToClassProperties(taskOutput);
                return true;
            }
            else
            {
                Log.LogError("Output file not found");
                return false;
            }
        }

        /// <summary>
        ///  returns message importance according to its int value in the enum
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static MessageImportance ImportanceFromInt(int value)
        {
            return value switch
            {
                0 => MessageImportance.High,
                1 => MessageImportance.Normal,
                2 => MessageImportance.Low,
                _ => MessageImportance.Normal,
            };
        }
        /// <summary>
        /// Links logger functions to the WebAssembly module
        /// </summary>
        /// <param name="linker"></param>
        /// <param name="store"></param>
        /// <exception cref="Exception"></exception>
        protected void LinkLogFunctions(Linker linker, Store store)
        {
            linker.Define("msbuild-log", "LogMessage", Function.FromCallback(store, (Caller caller, int importance, int address, int length) =>
            {
                Memory memory = caller.GetMemory("memory") ?? throw new WasmtimeException("WebAssembly module did not export a memory.");
                string message = memory.ReadString(address, length);
                Log.LogMessage(ImportanceFromInt(importance), message);
            }));

            linker.Define("msbuild-log", "LogError", Function.FromCallback(store, (Caller caller, int address, int length) =>
            {
                Memory memory = caller.GetMemory("memory") ?? throw new WasmtimeException("WebAssembly module did not export a memory.");
                string message = memory.ReadString(address, length);
                Log.LogError(message);
            }));

            linker.Define("msbuild-log", "LogWarning", Function.FromCallback(store, (Caller caller, int address, int length) =>
            {
                Memory memory = caller.GetMemory("memory") ?? throw new WasmtimeException("WebAssembly module did not export a memory.");
                string message = memory.ReadString(address, length);
                Log.LogWarning(message);
            }));
            Log.LogMessage(MessageImportance.Low, "Linked logger functions to WebAssembly module.");
        }

        private void ReflectJsonPropertyToClassProperty(PropertyInfo[] classProperties, JsonProperty jsonProperty)
        {
            // Find the matching property in the class
            PropertyInfo classProperty = Array.Find(classProperties, p => p.Name.Equals(jsonProperty.Name, StringComparison.OrdinalIgnoreCase));

            if (classProperty == null || !classProperty.CanWrite)
            {
                Log.LogMessage(MessageImportance.Normal, $"Property outupted by WasmTask {jsonProperty.Name} not found or is read-only.");
                return;
            }
            // Parse and set the property value based on its type
            // note: can't use switch because Type is not a constant
            if (classProperty.PropertyType == typeof(string))
            {
                classProperty.SetValue(this, jsonProperty.Value.GetString());
            }
            else if (classProperty.PropertyType == typeof(bool))
            {
                classProperty.SetValue(this, jsonProperty.Value.GetBoolean());
            }
            else if (classProperty.PropertyType == typeof(ITaskItem))
            {
                classProperty.SetValue(this, ExtractTaskItem(jsonProperty.Value));
            }
            else if (classProperty.PropertyType == typeof(ITaskItem[]))
            {
                classProperty.SetValue(this, ExtractTaskItems(jsonProperty.Value));
            }
            else if (classProperty.PropertyType == typeof(string[]))
            {
                classProperty.SetValue(this, jsonProperty.Value.EnumerateArray().Select(j => j.GetString()).ToArray());
            }
            else if (classProperty.PropertyType == typeof(bool[]))
            {
                classProperty.SetValue(this, jsonProperty.Value.EnumerateArray().Select(j => j.GetBoolean()).ToArray());
            }
            else
            {
                throw new ArgumentException($"Unsupported property type: {classProperty.PropertyType}"); // this should never happen, only programming error in this class or factory could cause it.
            }

        }

        /// <summary>
        /// Task Output JSON is in the form of a dictionary of output properties P.
        /// Read all keys of the JSON, values can be strings, dict<string,string> (representing ITaskItem), or bool, and lists of these things.
        /// Do Reflection for this class to obtain class properties R.
        /// for each property in P
        /// set R to value(P) if P==R
        /// </summary>
        /// <param name="taskOutputJson">Task Output JSON</param>
        private void ReflectOutputJsonToClassProperties(string taskOutputJson)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(taskOutputJson);
                JsonElement root = document.RootElement;

                // Get all properties of the current class
                PropertyInfo[] classProperties = GetType().GetProperties();

                // Iterate through all properties in the JSON
                foreach (JsonProperty jsonProperty in root.EnumerateObject())
                {
                    ReflectJsonPropertyToClassProperty(classProperties, jsonProperty);
                }
            }
            catch (JsonException ex)
            {
                Log.LogError($"Error parsing JSON: {ex.Message}");
            }
            catch (Exception ex)
            {
                Log.LogError($"Error Reflecting properties from Json to Class: {ex.Message}");
            }
        }

        private ITaskItem ExtractTaskItem(JsonElement jsonElement)
        {
            string itemSpec = jsonElement.GetProperty("ItemSpec").GetString();
            // TODO: Actually extract
            string itemPath = Path.Combine(_sharedTmpDir.FullName, itemSpec);
            if (!File.Exists(itemPath))
            {
                Log.LogError($"Task output file {itemPath} not found.");// TODO is this ok?
            }


            return new TaskItem(itemSpec);
        }
        private ITaskItem[] ExtractTaskItems(JsonElement jsonElement)
        {
            return jsonElement.EnumerateArray().Select(ExtractTaskItem).ToArray();
        }

        /// <summary>
        /// The task requires a function "TaskInfo" to be present in the WebAssembly module, it's used only in the factory to get the task properties.
        /// </summary>
        protected static void LinkTaskInfo(Linker linker, Store store) => linker.Define("msbuild-taskinfo", "TaskInfo", Function.FromCallback(store, (Caller caller, int address, int length) => { /* should do nothing in execution */ }));

    }
}
