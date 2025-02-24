﻿//
// Copyright (c) .NET Foundation and Contributors
// Portions Copyright (c) Microsoft Corporation.  All rights reserved.
// See LICENSE file in the project root for full license information.
//

using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using nanoFramework.TestAdapter;
using nanoFramework.Tools.Debugger;
using nanoFramework.Tools.Debugger.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace nanoFramework.TestPlatform.TestAdapter
{
    /// <summary>
    /// An Executor class
    /// </summary>
    [ExtensionUri(TestsConstants.NanoExecutor)]
    class Executor : ITestExecutor
    {
        private const string TestPassed = "Test passed: ";
        private const string TestFailed = "Test failed: ";
        private const string TestSkipped = "Test skipped: ";
        private const string Exiting = "Exiting.";
        private const string Done = "Done.";
        private Settings _settings;
        private LogMessenger _logger;
        private Process _nanoClr;

        // number of retries when performing a deploy operation
        private const int _numberOfRetries = 5;

        // timeout when performing a deploy operation
        private const int _timeoutMiliseconds = 1000;

        /// test session timeout (from the runsettings file)
        private int _testSessionTimeout = 300000;

        private IFrameworkHandle _frameworkHandle = null;

        /// <inheritdoc/>
        public void Cancel()
        {
            try
            {
                if (!_nanoClr.HasExited)
                {
                    _logger.LogMessage(
                        "Canceling to test process. Attempting to kill nanoCLR process...",
                        Settings.LoggingLevel.Verbose);

                    _nanoClr.Kill();
                    // Wait 5 seconds maximum
                    _nanoClr.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogPanicMessage($"Exception thrown while killing the process: {ex}");
            }
        }

        /// <inheritdoc/>
        public void RunTests(IEnumerable<string> sources, IRunContext runContext, IFrameworkHandle frameworkHandle)
        {
            try
            {
                InitializeLogger(runContext, frameworkHandle);
                foreach (var source in sources)
                {
                    var testsCases = TestDiscoverer.FindTestCases(source);

                    RunTests(testsCases, runContext, frameworkHandle);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogPanicMessage($"Exception raised in the process: {ex}");
            }
        }

        /// <inheritdoc/>
        public void RunTests(IEnumerable<TestCase> tests, IRunContext runContext, IFrameworkHandle frameworkHandle)
        {
            try
            {
                InitializeLogger(runContext, frameworkHandle);
                var uniqueSources = tests.Select(m => m.Source).Distinct();

                _logger.LogMessage(
                    "Test sources enumerated",
                    Settings.LoggingLevel.Verbose);

                foreach (var source in uniqueSources)
                {
                    var groups = tests.Where(m => m.Source == source);

                    _logger.LogMessage(
                        $"Test group is '{source}'",
                        Settings.LoggingLevel.Detailed);

                    List<TestResult> results;

                    if (_settings.IsRealHardware)
                    {
                        // we are connecting to a real device
                        results = RunTestOnHardwareAsync(groups.ToList()).GetAwaiter().GetResult();
                    }
                    else
                    {
                        // we are connecting to WIN32 nanoCLR
                        results = RunTestOnEmulator(groups.ToList());
                    }

                    foreach (var result in results)
                    {
                        frameworkHandle.RecordResult(result);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogPanicMessage($"Exception raised in the process: {ex}");
            }
        }

        private void InitializeLogger(IRunContext runContext, IFrameworkHandle frameworkHandle)
        {
            if (_logger == null)
            {
                var settingsProvider = runContext.RunSettings.GetSettings(TestsConstants.SettingsName) as SettingsProvider;

                _logger = new LogMessenger(frameworkHandle, settingsProvider);

                if (settingsProvider != null)
                {
                    // get TestSessionTimeout from runsettings
                    var xml = new XmlDocument();
                    xml.LoadXml(runContext.RunSettings.SettingsXml);
                    var timeout = xml.SelectSingleNode("RunSettings//RunConfiguration//TestSessionTimeout");
                    if (timeout != null && timeout.NodeType == XmlNodeType.Element)
                    {
                        int.TryParse(timeout.InnerText, out _testSessionTimeout);
                    }

                    _settings = settingsProvider.Settings;

                    _logger.LogMessage(
                        "Getting ready to run tests...",
                        Settings.LoggingLevel.Detailed);

                    _logger.LogMessage(
                        "Settings parsed",
                        Settings.LoggingLevel.Verbose);
                }
                else
                {
                    _logger.LogMessage(
                        "Getting ready to run tests...",
                        Settings.LoggingLevel.Detailed);

                    _logger.LogMessage(
                        "No settings for nanoFramework adapter",
                        Settings.LoggingLevel.Verbose);
                }
            }
        }

        private async Task<List<TestResult>> RunTestOnHardwareAsync(List<TestCase> tests)
        {
            _logger.LogMessage(
                "Setting up test runner in *** CONNECTED DEVICE ***",
                Settings.LoggingLevel.Detailed);

            List<TestResult> results = PrepareListResult(tests);
            List<byte[]> assemblies = new List<byte[]>();
            int retryCount = 0;

            var serialDebugClient = PortBase.CreateInstanceForSerial(true, 2000);

        retryConnection:
            
            if(string.IsNullOrEmpty(_settings.RealHardwarePort))
            {
                _logger.LogMessage($"Waiting for device enumeration to complete.", Settings.LoggingLevel.Verbose);
            }
            else
            {
                _logger.LogMessage($"Checking device on port {_settings.RealHardwarePort}.", Settings.LoggingLevel.Verbose);
            }

            while (!serialDebugClient.IsDevicesEnumerationComplete)
            {
                Thread.Sleep(1);
            }

            _logger.LogMessage($"Found: {serialDebugClient.NanoFrameworkDevices.Count} devices", Settings.LoggingLevel.Verbose);

            if (serialDebugClient.NanoFrameworkDevices.Count == 0)
            {
                if (retryCount > _numberOfRetries)
                {
                    results.First().Outcome = TestOutcome.Failed;
                    results.First().ErrorMessage = $"Couldn't find any device, please try to disable the device scanning in the Visual Studio Extension! If the situation persists reboot the device as well.";
                    return results;
                }
                else
                {
                    retryCount++;
                    serialDebugClient.ReScanDevices();
                    goto retryConnection;
                }
            }

            retryCount = 0;
            NanoDeviceBase device;

            if (serialDebugClient.NanoFrameworkDevices.Count > 1
                && !string.IsNullOrEmpty(_settings.RealHardwarePort))
            {
                // get the device at the requested COM port (if there is one)
                device = serialDebugClient.NanoFrameworkDevices.FirstOrDefault(m => m.SerialNumber == _settings.RealHardwarePort);

                // sanity check
                if(device is null)
                {
                    // no device, done here
                    _logger.LogMessage($"No device available at {_settings.RealHardwarePort}.", Settings.LoggingLevel.Verbose);
                    return results;
                }
            }
            else
            {
                // no COM port requested, just grab the 1st one
                device = serialDebugClient.NanoFrameworkDevices[0];
            }

            _logger.LogMessage(
                $"Getting things ready with {device.Description}",
                Settings.LoggingLevel.Detailed);

            // check if debugger engine exists
            if (device.DebugEngine == null)
            {
                device.CreateDebugEngine();
                _logger.LogMessage($"Debug engine created.", Settings.LoggingLevel.Verbose);
            }

            bool deviceIsInInitializeState = false;

        retryDebug:
            bool connectResult = device.DebugEngine.Connect(5000, true, true);
            _logger.LogMessage($"Device connect result is {connectResult}. Attempt {retryCount}/{_numberOfRetries}", Settings.LoggingLevel.Verbose);

            if (!connectResult)
            {
                if (retryCount < _numberOfRetries)
                {
                    // Give it a bit of time
                    await Task.Delay(100);
                    retryCount++;

                    goto retryDebug;
                }
                else
                {
                    results.First().Outcome = TestOutcome.Failed;
                    results.First().ErrorMessage = $"Couldn't connect to the device, please try to disable the device scanning in the Visual Studio Extension! If the situation persists reboot the device as well.";
                    return results;
                }
            }

            retryCount = 0;
        retryErase:
            // erase the device
            _logger.LogMessage($"Erase deployment block storage. Attempt {retryCount}/{_numberOfRetries}.", Settings.LoggingLevel.Verbose);

            var eraseResult = device.Erase(
                    EraseOptions.Deployment,
                    null,
                    null);

            _logger.LogMessage($"Erase result is {eraseResult}.", Settings.LoggingLevel.Verbose);
            if (!eraseResult)
            {
                if (retryCount < _numberOfRetries)
                {
                    // Give it a bit of time
                    await Task.Delay(400);
                    retryCount++;
                    goto retryErase;
                }
                else
                {
                    results.First().Outcome = TestOutcome.Failed;
                    results.First().ErrorMessage = $"Couldn't erase the device, please try to disable the device scanning in the Visual Studio Extension! If the situation persists reboot the device as well.";
                    return results;
                }
            }

            retryCount = 0;
            // initial check 
            if (device.DebugEngine.IsDeviceInInitializeState())
            {
                _logger.LogMessage($"Device status verified as being in initialized state. Requesting to resume execution. Attempt {retryCount}/{_numberOfRetries}.", Settings.LoggingLevel.Error);
                // set flag
                deviceIsInInitializeState = true;

                // device is still in initialization state, try resume execution
                device.DebugEngine.ResumeExecution();
            }

            // handle the workflow required to try resuming the execution on the device
            // only required if device is not already there
            // retry 5 times with a 500ms interval between retries
            while (retryCount++ < _numberOfRetries && deviceIsInInitializeState)
            {
                if (!device.DebugEngine.IsDeviceInInitializeState())
                {
                    _logger.LogMessage($"Device has completed initialization.", Settings.LoggingLevel.Verbose);
                    // done here
                    deviceIsInInitializeState = false;
                    break;
                }

                _logger.LogMessage($"Waiting for device to report initialization completed ({retryCount}/{_numberOfRetries}).", Settings.LoggingLevel.Verbose);
                // provide feedback to user on the 1st pass
                if (retryCount == 0)
                {
                    _logger.LogMessage($"Waiting for device to initialize.", Settings.LoggingLevel.Verbose);
                }

                if (device.DebugEngine.IsConnectedTonanoBooter)
                {
                    _logger.LogMessage($"Device reported running nanoBooter. Requesting to load nanoCLR.", Settings.LoggingLevel.Verbose);
                    // request nanoBooter to load CLR
                    device.DebugEngine.ExecuteMemory(0);
                }
                else if (device.DebugEngine.IsConnectedTonanoCLR)
                {
                    _logger.LogMessage($"Device reported running nanoCLR. Requesting to reboot nanoCLR.", Settings.LoggingLevel.Error);
                    await Task.Run(delegate
                    {
                        // already running nanoCLR try rebooting the CLR
                        device.DebugEngine.RebootDevice(RebootOptions.ClrOnly);
                    });
                }

                // wait before next pass
                // use a back-off strategy of increasing the wait time to accommodate slower or less responsive targets (such as networked ones)
                await Task.Delay(TimeSpan.FromMilliseconds(_timeoutMiliseconds * (retryCount + 1)));

                await Task.Yield();
            }

            // check if device is still in initialized state
            if (!deviceIsInInitializeState)
            {
                // device has left initialization state
                _logger.LogMessage($"Device is initialized and ready!", Settings.LoggingLevel.Verbose);
                await Task.Yield();


                //////////////////////////////////////////////////////////
                // sanity check for devices without native assemblies ?!?!
                if (device.DeviceInfo.NativeAssemblies.Count == 0)
                {
                    _logger.LogMessage($"Device reporting no assemblies loaded. This can not happen. Sanity check failed.", Settings.LoggingLevel.Error);
                    // there are no assemblies deployed?!
                    results.First().Outcome = TestOutcome.Failed;
                    results.First().ErrorMessage = $"Couldn't find any native assemblies deployed in {device.Description}, {device.TargetName} on {device.SerialNumber}! If the situation persists reboot the device.";
                    return results;
                }

                _logger.LogMessage($"Computing deployment blob.", Settings.LoggingLevel.Verbose);

                // build a list with the full path for each DLL, referenced DLL and EXE
                List<DeploymentAssembly> assemblyList = new List<DeploymentAssembly>();

                var source = tests.First().Source;
                var workingDirectory = Path.GetDirectoryName(source);
                var allPeFiles = Directory.GetFiles(workingDirectory, "*.pe");

                var decompilerSettings = new DecompilerSettings
                {
                    LoadInMemory = false,
                    ThrowOnAssemblyResolveErrors = false
                };

                foreach (string assemblyPath in allPeFiles)
                {
                    // load assembly in order to get the versions
                    var file = Path.Combine(workingDirectory, assemblyPath.Replace(".pe", ".dll"));
                    if (!File.Exists(file))
                    {
                        // Check with an exe
                        file = Path.Combine(workingDirectory, assemblyPath.Replace(".pe", ".exe"));
                    }

                    var decompiler = new CSharpDecompiler(file, decompilerSettings); ;
                    var assemblyProperties = decompiler.DecompileModuleAndAssemblyAttributesToString();

                    // AssemblyVersion
                    string pattern = @"(?<=AssemblyVersion\("")(.*)(?=\""\)])";
                    var match = Regex.Matches(assemblyProperties, pattern, RegexOptions.IgnoreCase);
                    string assemblyVersion = match[0].Value;

                    // AssemblyNativeVersion
                    pattern = @"(?<=AssemblyNativeVersion\("")(.*)(?=\""\)])";
                    match = Regex.Matches(assemblyProperties, pattern, RegexOptions.IgnoreCase);

                    // only class libs have this attribute, therefore sanity check is required
                    string nativeVersion = "";
                    if (match.Count == 1)
                    {
                        nativeVersion = match[0].Value;
                    }

                    assemblyList.Add(new DeploymentAssembly(Path.Combine(workingDirectory, assemblyPath), assemblyVersion, nativeVersion));
                }

                _logger.LogMessage($"Added {assemblyList.Count} assemblies to deploy.", Settings.LoggingLevel.Verbose);
                await Task.Yield();

                // Keep track of total assembly size
                long totalSizeOfAssemblies = 0;

                // now we will re-deploy all system assemblies
                foreach (DeploymentAssembly peItem in assemblyList)
                {
                    // append to the deploy blob the assembly
                    using (FileStream fs = File.Open(peItem.Path, FileMode.Open, FileAccess.Read))
                    {
                        long length = (fs.Length + 3) / 4 * 4;
                        _logger.LogMessage($"Adding {Path.GetFileNameWithoutExtension(peItem.Path)} v{peItem.Version} ({length} bytes) to deployment bundle", Settings.LoggingLevel.Verbose);
                        byte[] buffer = new byte[length];

                        await Task.Yield();

                        await fs.ReadAsync(buffer, 0, (int)fs.Length);
                        assemblies.Add(buffer);

                        // Increment totalizer
                        totalSizeOfAssemblies += length;
                    }
                }

                _logger.LogMessage($"Deploying {assemblyList.Count:N0} assemblies to device... Total size in bytes is {totalSizeOfAssemblies}.", Settings.LoggingLevel.Verbose);
                // need to keep a copy of the deployment blob for the second attempt (if needed)
                var assemblyCopy = new List<byte[]>(assemblies);

                await Task.Yield();

                var deploymentLogger = new Progress<string>((m) => _logger.LogMessage(m, Settings.LoggingLevel.Detailed));

                await Task.Run(async delegate
                {
                    // OK to skip erase as we just did that
                    // no need to reboot device
                    if (!device.DebugEngine.DeploymentExecute(
                        assemblyCopy,
                        false,
                        false,
                        null,
                        deploymentLogger))
                    {
                        // if the first attempt fails, give it another try

                        // wait before next pass
                        await Task.Delay(TimeSpan.FromSeconds(1));

                        await Task.Yield();

                        _logger.LogMessage("Deploying assemblies. Second attempt.", Settings.LoggingLevel.Verbose);

                        // !! need to use the deployment blob copy
                        assemblyCopy = new List<byte[]>(assemblies);

                        // can't skip erase as we just did that
                        // no need to reboot device
                        if (!device.DebugEngine.DeploymentExecute(
                            assemblyCopy,
                            false,
                            false,
                            null,
                            deploymentLogger))
                        {
                            _logger.LogMessage("Deployment failed.", Settings.LoggingLevel.Error);

                            // throw exception to signal deployment failure
                            results.First().Outcome = TestOutcome.Failed;
                            results.First().ErrorMessage = $"Deployment failed in {device.Description}, {device.TargetName} on {device.SerialNumber}! If the situation persists reboot the device.";
                        }
                    }
                });

                await Task.Yield();
                // If there has been an issue before, the first test is marked as failed
                if (results.First().Outcome == TestOutcome.Failed)
                {
                    return results;
                }

                StringBuilder output = new StringBuilder();
                bool isFinished = false;
                // attach listener for messages
                device.DebugEngine.OnMessage += (message, text) =>
                {
                    _logger.LogMessage(text, Settings.LoggingLevel.Verbose);
                    output.Append(text);
                    if (text.Contains(Done))
                    {
                        isFinished = true;
                    }
                };

                device.DebugEngine.RebootDevice(RebootOptions.ClrOnly);

                while (!isFinished)
                {
                    Thread.Sleep(1);
                }

                _logger.LogMessage($"Tests finished.", Settings.LoggingLevel.Verbose);
                CheckAllTests(output.ToString(), results);
            }
            else
            {
                _logger.LogMessage("Failed to initialize device.", Settings.LoggingLevel.Error);
            }


            return results;
        }

        private List<TestResult> PrepareListResult(List<TestCase> tests)
        {
            List<TestResult> results = new List<TestResult>();

            foreach (var test in tests)
            {
                TestResult result = new TestResult(test) { Outcome = TestOutcome.None };
                results.Add(result);
            }

            return results;
        }

        private List<TestResult> RunTestOnEmulator(List<TestCase> tests)
        {
            _logger.LogMessage(
                "Setting up test runner in *** nanoCLR WIN32***",
                Settings.LoggingLevel.Detailed);

            _logger.LogMessage(
                $"Timeout set to {_testSessionTimeout}ms",
                Settings.LoggingLevel.Verbose);

            List<TestResult> results = PrepareListResult(tests);

            _logger.LogMessage(
                "Processing assemblies to load into test runner...",
                Settings.LoggingLevel.Verbose);

            var source = tests.First().Source;
            var workingDirectory = Path.GetDirectoryName(source);
            var allPeFiles = Directory.GetFiles(workingDirectory, "*.pe");

            // prepare the process start of the WIN32 nanoCLR
            _nanoClr = new Process();

            AutoResetEvent outputWaitHandle = new AutoResetEvent(false);
            AutoResetEvent errorWaitHandle = new AutoResetEvent(false);
            StringBuilder output = new StringBuilder();
            StringBuilder error = new StringBuilder();

            try
            {
                // prepare parameters to load nanoCLR, include:
                // 1. unit test launcher
                // 2. mscorlib
                // 3. test framework
                // 4. test application
                StringBuilder str = new StringBuilder();
                foreach (var pe in allPeFiles)
                {
                    str.Append($" -load {Path.Combine(workingDirectory, pe)}");
                }

                string parameter = str.ToString();

                _logger.LogMessage(
                    $"Parameters to pass to nanoCLR: <{parameter}>",
                    Settings.LoggingLevel.Verbose);

                var nanoClrLocation = TestObjectHelper.GetNanoClrLocation();
                if (string.IsNullOrEmpty(nanoClrLocation))
                {
                    _logger.LogPanicMessage("Can't find nanoCLR Win32 in any of the directories!");
                    results.First().Outcome = TestOutcome.Failed;
                    results.First().ErrorMessage = "Can't find nanoCLR Win32 in any of the directories!";
                    return results;
                }

                _logger.LogMessage($"Found nanoCLR Win32: {nanoClrLocation}", Settings.LoggingLevel.Verbose);
                _nanoClr.StartInfo = new ProcessStartInfo(nanoClrLocation, parameter)
                {
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                _logger.LogMessage(
                    $"Launching process with nanoCLR (from {Path.GetFullPath(TestObjectHelper.GetNanoClrLocation())})",
                    Settings.LoggingLevel.Verbose);

                // launch nanoCLR
                if (!_nanoClr.Start())
                {
                    results.First().Outcome = TestOutcome.Failed;
                    results.First().ErrorMessage = "Failed to start nanoCLR";

                    _logger.LogPanicMessage(
                        "Failed to start nanoCLR!");
                }

                _nanoClr.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data == null)
                    {
                        outputWaitHandle.Set();
                    }
                    else
                    {
                        output.AppendLine(e.Data);
                    }
                };

                _nanoClr.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data == null)
                    {
                        errorWaitHandle.Set();
                    }
                    else
                    {
                        error.AppendLine(e.Data);
                    }
                };

                _nanoClr.Start();

                _nanoClr.BeginOutputReadLine();
                _nanoClr.BeginErrorReadLine();

                _logger.LogMessage(
                    $"nanoCLR started @ process ID: {_nanoClr.Id}",
                    Settings.LoggingLevel.Detailed);


                // wait for exit, no worries about the outcome
                _nanoClr.WaitForExit(_testSessionTimeout);

                CheckAllTests(output.ToString(), results);
                _logger.LogMessage(output.ToString(), Settings.LoggingLevel.Verbose);
                if (!output.ToString().Contains(Done))
                {
                    results.First().Outcome = TestOutcome.Failed;
                    results.First().ErrorMessage = output.ToString();
                }

                var notPassedOrFailed = results.Where(m => m.Outcome != TestOutcome.Failed && m.Outcome != TestOutcome.Passed && m.Outcome != TestOutcome.Skipped);
                if (notPassedOrFailed.Any())
                {
                    notPassedOrFailed.First().ErrorMessage = output.ToString();
                }

            }
            catch (Exception ex)
            {
                _logger.LogMessage(
                    $"Fatal exception when processing test results: >>>{ex.Message}\r\n{output}\r\n{error}",
                    Settings.LoggingLevel.Detailed);

                results.First().Outcome = TestOutcome.Failed;
                results.First().ErrorMessage = $"Fatal exception when processing test results. Set logging to 'Detailed' for details.";
            }
            finally
            {
                if (!_nanoClr.HasExited)
                {
                    _logger.LogMessage(
                        "Attempting to kill nanoCLR process...",
                        Settings.LoggingLevel.Verbose);

                    _nanoClr.Kill();
                    _nanoClr.WaitForExit(2000);
                }
            }

            return results;
        }

        private void CheckAllTests(string rawOutput, List<TestResult> results)
        {
            var outputStrings = Regex.Replace(
                rawOutput,
                @"^\s+$[\r\n]*",
                "",
                RegexOptions.Multiline).Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            _logger.LogMessage(
                "Parsing test results...",
                Settings.LoggingLevel.Verbose);

            StringBuilder testOutput = new StringBuilder();

            bool readyFound = false;

            foreach (var line in outputStrings)
            {
                if (line.Contains(TestPassed))
                {
                    // Format is "Test passed: MethodName, ticks";
                    // We do get split with space if the coma is missing, happens time to time

                    string method = line.Substring(line.IndexOf(TestPassed) + TestPassed.Length).Split(',')[0].Split(' ')[0];
                    string ticks = line.Substring(line.IndexOf(TestPassed) + TestPassed.Length + method.Length + 2);

                    long ticksNum = 0;
                    long.TryParse(ticks, out ticksNum);

                    // Find the test
                    var res = results.FirstOrDefault(m => m.TestCase.DisplayName == method);
                    if (res != null)
                    {
                        res.Duration = TimeSpan.FromTicks(ticksNum);
                        res.Outcome = TestOutcome.Passed;
                        res.Messages.Add(new TestResultMessage(
                            TestResultMessage.StandardOutCategory,
                            testOutput.ToString()));
                    }

                    // reset test output
                    testOutput = new StringBuilder();
                }
                else if (line.Contains(TestFailed))
                {
                    // Format is "Test failed: MethodName, Exception message";

                    string method = line.Substring(line.IndexOf(TestFailed) + TestFailed.Length).Split(',')[0].Split(' ')[0];

                    string exception = line.Substring(line.IndexOf(TestFailed) + TestPassed.Length + method.Length + 2);

                    // Find the test
                    var res = results.FirstOrDefault(m => m.TestCase.DisplayName == method);
                    if (res != null)
                    {
                        res.ErrorMessage = exception;
                        res.Outcome = TestOutcome.Failed;
                        res.Messages.Add(new TestResultMessage(
                            TestResultMessage.StandardErrorCategory,
                            testOutput.ToString()));
                    }

                    // reset test output
                    testOutput = new StringBuilder();
                }
                else if (line.Contains(TestSkipped))
                {
                    // Format is "Test failed: MethodName, Exception message";

                    string method = line.Substring(line.IndexOf(TestSkipped) + TestSkipped.Length).Split(',')[0].Split(' ')[0];

                    string exception = line.Substring(line.IndexOf(TestSkipped) + TestPassed.Length + method.Length + 2);

                    // Find the test
                    var res = results.FirstOrDefault(m => m.TestCase.DisplayName == method);
                    if (res != null)
                    {
                        res.ErrorMessage = exception;
                        res.Outcome = TestOutcome.Skipped;
                        res.Messages.Add(new TestResultMessage(
                            TestResultMessage.StandardErrorCategory,
                            testOutput.ToString()));

                        // If this is a Steup Test, set all the other tests from the class to skipped as well
                        var trait = res.TestCase.Traits.FirstOrDefault();
                        if (trait != null)
                        {
                            if (trait.Value == "Setup" && trait.Name == "Type")
                            {
                                // A test name is the full qualify name of the metho.methodname, finding the list . index will give all the familly name
                                var testCasesToSkipName = res.TestCase.FullyQualifiedName.Substring(0, res.TestCase.FullyQualifiedName.LastIndexOf('.'));
                                var allTestToSkip = results.Where(m => m.TestCase.FullyQualifiedName.Contains(testCasesToSkipName));
                                foreach (var testToSkip in allTestToSkip)
                                {
                                    if(testToSkip.TestCase.DisplayName == method)
                                    {
                                        continue;
                                    }

                                    testToSkip.Outcome = TestOutcome.Skipped;
                                    res.Messages.Add(new TestResultMessage(
                                        TestResultMessage.StandardErrorCategory,
                                        $"Setup method '{method}' has been skipped."));
                                }
                            }
                        }
                    }

                    // reset test output
                    testOutput = new StringBuilder();
                }
                else
                {
                    if (readyFound)
                    {
                        testOutput.AppendLine(line);

                        continue;
                    }

                    if (line.StartsWith("Ready."))
                    {
                        readyFound = true;
                    }
                }
            }
        }
    }
}
