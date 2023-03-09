﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

namespace MicrobenchmarkGui
{
    public static class OpenCLTest
    {
        private static List<OpenCLDevice> openCLDevices;
        private static string[] openCLPlatforms;

        // for text results display
        private static string[][] formattedResults;
        private static string[] cols = { "Test Size", "Latency" };
        private static string[] localCols = { "Item", "Latency" };

        // for graph
        private static List<float> floatTestPoints;
        private static List<float> testResultsList;

        public static uint DefaultGpuPointerChasingStride = 64;

        public static uint[] testSizes = { 2, 4, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256, 512, 600, 768, 1024, 1536, 2048,
                               3072, 4096, 5120, 6144, 8192, 10240, 12288, 16384, 24567, 32768, 65536, 98304,
                               131072, 262144, 393216, 524288, 1048576 };

        public static Dictionary<string, List<Tuple<float, float>>> RunResults;

        public static string InitializeDeviceControls(FlowLayoutPanel deviceFlowLayoutPanel)
        {
            // what can we fit in the GUI
            int maxNameLength = 128;

            // Enumerate platforms and devices
            int platformCount = BenchmarkInteropFunctions.GetPlatformCount();
            openCLPlatforms = new string[platformCount];
            openCLDevices = new List<OpenCLDevice>();

            int totalDeviceCount = 0;
            IntPtr nameBuffer = Marshal.AllocHGlobal(maxNameLength);
            for (int platformIdx = 0; platformIdx < platformCount; platformIdx++)
            {
                int platformDeviceCount = BenchmarkInteropFunctions.GetDeviceCount(platformIdx);
                totalDeviceCount += platformDeviceCount;
                BenchmarkInteropFunctions.GetPlatformName(platformIdx, nameBuffer, maxNameLength);
                openCLPlatforms[platformIdx] = Marshal.PtrToStringAnsi(nameBuffer);
                for (int deviceIdx = 0; deviceIdx < platformDeviceCount; deviceIdx++)
                {
                    OpenCLDevice device = new OpenCLDevice();
                    device.PlatformIndex = platformIdx;
                    device.DeviceIndex = deviceIdx;
                    BenchmarkInteropFunctions.GetDeviceName(platformIdx, deviceIdx, nameBuffer, maxNameLength);
                    device.DeviceName = Marshal.PtrToStringAnsi(nameBuffer);
                    device.DeviceName = device.DeviceName.Trim();
                    openCLDevices.Add(device);
                }
            }

            Marshal.FreeHGlobal(nameBuffer);

            deviceFlowLayoutPanel.Controls.Clear();
            deviceFlowLayoutPanel.SuspendLayout();

            // Assemble controls. We populate the list by platforms, sequentially
            int currentPlatform = -1;
            bool firstDevice = true, firstPlatform = true;
            Size groupBoxRadioButtonSize = new Size(220, 17);
            foreach (OpenCLDevice clDevice in openCLDevices)
            {
                if (clDevice.PlatformIndex != currentPlatform)
                {
                    currentPlatform = clDevice.PlatformIndex;
                    Label platformLabel = new Label();
                    platformLabel.Text = openCLPlatforms[clDevice.PlatformIndex] + ":";
                    platformLabel.Name = $"platform{clDevice.PlatformIndex}Label";
                    platformLabel.AutoSize = true;
                    if (firstPlatform)
                    {
                        firstPlatform = false;
                    }
                    else
                    {
                        platformLabel.Margin = new Padding(0, 10, 0, 0);
                    }

                    deviceFlowLayoutPanel.Controls.Add(platformLabel);
                }

                RadioButton deviceButton = new RadioButton();
                deviceButton.Text = clDevice.DeviceName;
                deviceButton.Name = $"platform{clDevice.PlatformIndex}dev{clDevice.DeviceIndex}RadioButton";
                deviceButton.AutoSize = true;
                if (firstDevice)
                {
                    deviceButton.Checked = true;
                    firstDevice = false;
                }

                deviceFlowLayoutPanel.Controls.Add(deviceButton);
                clDevice.DeviceButton = deviceButton;
            }

            deviceFlowLayoutPanel.ResumeLayout();
            deviceFlowLayoutPanel.PerformLayout();

            if (RunResults == null) RunResults = new Dictionary<string, List<Tuple<float, float>>>();
            return $"System has {totalDeviceCount} OpenCL devices across {platformCount} platforms";
        }

        public static void RunLatencyTest(MicrobenchmarkForm.SafeSetResultListView setListViewDelegate,
            MicrobenchmarkForm.SafeSetResultListViewColumns setListViewColsDelegate,
            MicrobenchmarkForm.SafeSetResultsChart setChartDelegate,
            MicrobenchmarkForm.SafeSetProgressLabel setLabelDelegate,
            ListView resultListView,
            Chart resultChart,
            Label progressLabel,
            BenchmarkInteropFunctions.CLTestType testMode,
            uint stride,
            CancellationToken runCancel)
        {
            // figure out which device is checked
            string testLabel = "undef";
            int platformIndex = -1, deviceIndex = -1;
            foreach (OpenCLDevice clDevice in openCLDevices)
            {
                if (clDevice.DeviceButton != null && clDevice.DeviceButton.Checked)
                {
                    platformIndex = clDevice.PlatformIndex;
                    deviceIndex = clDevice.DeviceIndex;
                    testLabel = clDevice.DeviceName + ", " + testMode.ToString();
                    if (stride != DefaultGpuPointerChasingStride)
                    {
                        testLabel += $", {stride}B Stride";
                    }
                }
            }

            if (platformIndex == -1 || deviceIndex == -1)
            {
                progressLabel.Invoke(setLabelDelegate, new object[] { "No OpenCL device selected" });
                return;
            }

            int rc = BenchmarkInteropFunctions.SetOpenCLContext(platformIndex, deviceIndex);
            if (rc < 0)
            {
                progressLabel.Invoke(setLabelDelegate, new object[] { "Could not create OpenCL context and command queue for selected device" });
                return;
            }

            // Fermi can't build the texture kernel, so separate it out
            if (testMode == BenchmarkInteropFunctions.CLTestType.Texture) ExtractResourceFile("latencykernel_tex.cl");
            else ExtractResourceFile("latencykernel.cl");
            rc = BenchmarkInteropFunctions.InitializeLatencyTest(testMode);
            if (rc < 0)
            {
                progressLabel.Invoke(setLabelDelegate, new object[] { "Could not build OpenCL kernel for selected device" });
                return;
            }

            // Determine limits
            ulong maxTestSizeKb = 0;
            if (testMode == BenchmarkInteropFunctions.CLTestType.GlobalScalar || testMode == BenchmarkInteropFunctions.CLTestType.GlobalVector)
            {
                maxTestSizeKb = BenchmarkInteropFunctions.GetDeviceMaxBufferSize() / 1024;
            }
            else if (testMode == BenchmarkInteropFunctions.CLTestType.ConstantScalar)
            {
                maxTestSizeKb = BenchmarkInteropFunctions.GetDeviceMaxConstantBufferSize() / 1024;
            }
            else if (testMode == BenchmarkInteropFunctions.CLTestType.Texture)
            {
                maxTestSizeKb = BenchmarkInteropFunctions.GetDeviceMaxTextureSize() / 1024;
            }

            if (testMode == BenchmarkInteropFunctions.CLTestType.Local)
            {
                // Set GUI stuff for local latency test
                uint localTestSizeKb = 4; // keep in sync with latencykernel.cl code
                resultListView.Invoke(setListViewColsDelegate, new object[] { localCols });
                formattedResults = new string[1][];
                formattedResults[0] = new string[2];
                formattedResults[0][0] = "Local Mem";
                formattedResults[0][1] = "Not Run";
                resultListView.Invoke(setListViewDelegate, new object[] { formattedResults });

                // more than regular latency test because we expect this to be relatively fast
                uint currentIterations = 150000;

                // slightly higher targets for better accuracy. really risking that TDR limit
                float targetTimeMs = 2200, minTimeMs = 1500, lastTimeMs = 1;
                float result;
                do
                {
                    progressLabel.Invoke(setLabelDelegate, new object[] { $"Testing local memory latency, {currentIterations / 1000}K iterations" });
                    result = BenchmarkInteropFunctions.RunCLLatencyTest(localTestSizeKb, currentIterations, testMode);

                    // scale iterations to reach target time
                    lastTimeMs = (float)(result * currentIterations / 1e6);
                    ulong desiredIterations = TestUtilities.ScaleIterations(currentIterations, targetTimeMs, lastTimeMs);
                    if (desiredIterations > uint.MaxValue)
                    {
                        currentIterations = uint.MaxValue;
                    }
                    else
                    {
                        currentIterations = (uint)desiredIterations;
                    }
                } while (lastTimeMs < minTimeMs);

                formattedResults[0][1] = string.Format("{0:F2} ns", result);
                resultListView.Invoke(setListViewDelegate, new object[] { formattedResults });
            }
            else
            {
                // Set GUI stuff
                List<Tuple<float, float>> currentRunResults = new List<Tuple<float, float>>();
                testResultsList = new List<float>();
                floatTestPoints = new List<float>();
                resultListView.Invoke(setListViewColsDelegate, new object[] { cols });

                BenchmarkInteropFunctions.SetGpuPtrChasingStride(stride);

                List<uint> validTestSizes = new List<uint>();
                for (uint i = 0; i < testSizes.Length; i++)
                {
                    // no point if stride is so large you won't actually bounce around anything
                    if (testSizes[i] * 1024 < 2 * stride) continue;
                    if (testSizes[i] > maxTestSizeKb) break;
                    validTestSizes.Add(testSizes[i]);
                }

                uint[] validTestSizeArr = validTestSizes.ToArray();
                float[] testResults = new float[validTestSizeArr.Length];
                formattedResults = new string[validTestSizeArr.Length][];
                for (uint i = 0; i < validTestSizeArr.Length; i++)
                {
                    testResults[i] = 0;
                    formattedResults[i] = new string[2];
                    formattedResults[i][0] = string.Format("{0} KB", validTestSizeArr[i]);
                    formattedResults[i][1] = "Not Run";
                }
                resultListView.Invoke(setListViewDelegate, new object[] { formattedResults });

                // Run test
                bool failed = false;
                bool first = true;
                uint baseIterations = 50000;
                for (uint testIdx = 0; testIdx < validTestSizeArr.Length; testIdx++)
                {
                    if (runCancel.IsCancellationRequested)
                    {
                        break;
                    }

                    uint testSize = validTestSizeArr[testIdx];
                    uint currentIterations = baseIterations;
                    float targetTimeMs = 2000, minTimeMs = 1000, lastTimeMs = 1;
                    float result;

                    do
                    {
                        progressLabel.Invoke(setLabelDelegate, new object[] { $"Testing {testSize} KB with {(currentIterations / 1000)}K iterations (device limit is {maxTestSizeKb} KB). Last run = {lastTimeMs} ms" });
                        result = BenchmarkInteropFunctions.RunCLLatencyTest(testSize, currentIterations, testMode);
                        if (result < 0)
                        {
                            progressLabel.Invoke(setLabelDelegate, new object[] { $"Latency test with {testSize} KB failed" });
                            failed = true;
                            break;
                        }

                        // scale iterations to reach target time
                        lastTimeMs = (float)(result * currentIterations / 1e6);
                        ulong desiredIterations = TestUtilities.ScaleIterations(currentIterations, targetTimeMs, lastTimeMs);
                        if (desiredIterations > uint.MaxValue)
                        {
                            currentIterations = uint.MaxValue;
                        }
                        else
                        {
                            currentIterations = (uint)desiredIterations;
                        }
                    } while (lastTimeMs < minTimeMs);

                    if (first)
                    {
                        // run it a few more times because some GPUs take years to clock ramp
                        progressLabel.Invoke(setLabelDelegate, new object[] { $"Making sure GPU is warmed up: {testSize} KB with {(currentIterations / 1000)}K iterations (device limit is {maxTestSizeKb} KB)" });
                        for (int warmupRun = 0; warmupRun < 3; warmupRun++) result = BenchmarkInteropFunctions.RunCLLatencyTest(testSize, currentIterations, testMode);
                        first = false;
                    }

                    // Update result table
                    if (failed)
                    {
                        formattedResults[testIdx][1] = string.Format("{0:F2} ns", "(Fail)");
                        resultListView.Invoke(setListViewDelegate, new object[] { formattedResults });
                        break;
                    }

                    formattedResults[testIdx][1] = string.Format("{0:F2} ns", result);
                    resultListView.Invoke(setListViewDelegate, new object[] { formattedResults });

                    // Update chart
                    floatTestPoints.Add(testSize);
                    testResultsList.Add(result);
                    currentRunResults.Add(new Tuple<float, float>(testSize, result));
                    resultChart.Invoke(setChartDelegate, new object[] { testLabel, floatTestPoints.ToArray(), testResultsList.ToArray(), MicrobenchmarkForm.ResultChartType.GpuMemoryLatency });
                }

                progressLabel.Invoke(setLabelDelegate, new object[] { $"Run finished" });
                RunResults.Add(testLabel, currentRunResults);
            }

            rc = BenchmarkInteropFunctions.DeinitializeLatencyTest();
            if (rc < 0)
            {
                progressLabel.Invoke(setLabelDelegate, new object[] { "Could not clean up OpenCL state for selected device" });
                return;
            }
        }

        private static void ExtractResourceFile(string filename)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            foreach (string resourceName in assembly.GetManifestResourceNames())
            {
                if (resourceName.EndsWith(filename))
                {
                    Stream stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream != null)
                    {
                        byte[]  buffer = new byte[stream.Length];
                        stream.Read(buffer, 0, buffer.Length);
                        using (FileStream target = new FileStream(filename, FileMode.Create))
                        {
                            target.Write(buffer, 0, buffer.Length);
                            target.Flush();
                        }
                    }
                }
            }
        }

        public class OpenCLDevice
        {
            public int PlatformIndex;
            public int DeviceIndex;
            public string DeviceName;
            public RadioButton DeviceButton;
        }
    }
}
