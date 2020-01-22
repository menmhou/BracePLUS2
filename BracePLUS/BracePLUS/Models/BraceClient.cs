﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BracePLUS.Extensions;
using BracePLUS.Views;
using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.Exceptions;
using Plugin.BLE.Abstractions.Utils;
using Syncfusion.SfChart.XForms;
using Xamarin.Forms;

namespace BracePLUS.Models
{
    public class BraceClient
    {
        #region Model Properties
        // Bluetooth properties
        public IAdapter adapter;
        public IBluetoothLE ble;
        public IDevice brace;
        public IService service;
        public ICharacteristic menuCharacteristic;
        public ICharacteristic streamCharacteristic;

        public bool isStreaming;
        public bool isSaving;

        public string connectButtonText = "Connect";
        public string streamButtonText = "Stream";
        public string saveButtonText = "Save To SD";

        //DataObject dataObject;
        StackLayout stack;

        // DATA SIZE FOR MAGBOARD (+HEADER)
        byte[] buffer = new byte[128];
        byte[] SDBuf = new byte[512];

        public static ObservableCollection<string> files;

        // Bluetooth Definitions
        public Guid serviceGUID = Guid.Parse(Constants.serviceUUID);
        public Guid menuGUID = Guid.Parse(Constants.menuCharUUID);
        public Guid streamGUID = Guid.Parse(Constants.streamCharUUID);

        static Color debug = Color.Red;
        static Color info = Color.Blue;

        public int STATUS = Constants.IDLE;

        byte[] commsByte = new byte[64];
        string raw, msg = "";

        public List<string> messages;

        MessageHandler handler;

        int packetIndex;
        #endregion

        #region Model Instanciation
        // For use with DataPage Interactions
        public BraceClient()
        {
            this.handler = new MessageHandler();

            this.ble = CrossBluetoothLE.Current;
            this.adapter = CrossBluetoothLE.Current.Adapter;

            messages = new List<string>();

            ble.StateChanged += (s, e) =>
            {
                if (e.NewState.ToString() != "On")
                {
                    Debug.WriteLine($"The bluetooth state changed to {e.NewState}");
                    write(string.Format($"The bluetooth state changed to {e.NewState}"), debug);
                }
            };

            adapter.DeviceDiscovered += async (s, e) =>
            {
                string name = e.Device.Name;

                if (name != null)
                {
                    Debug.WriteLine(String.Format("Discovered device: {0}", name));
                    write(String.Format("Discovered device: {0}", name), info);

                    if (e.Device.Name == Constants.DEV_NAME)
                    {
                        brace = e.Device;
                        await Connect();
                    }
                }
            };
            adapter.DeviceConnectionLost += (s, e) => write("Disconnected from " + e.Device.Name, info);
            adapter.DeviceDisconnected += (s, e) => write("Disconnected from " + e.Device.Name, info);
        }

        public void RegisterStack(StackLayout s)
        {
            this.stack = s;
        }
        #endregion

        #region Model Client Logic Methods
        public async Task Connect()
        {
            if (!ble.IsOn)
            {
                await Application.Current.MainPage.DisplayAlert("Bluetooth off.", "Please turn on bluetooth to connect to devices.", "OK");
                return;
            }

            try
            {
                Debug.WriteLine("Attempting connection...");

                if (brace != null)
                {
                    STATUS = Constants.CLIENT_CONNECT;

                    await adapter.ConnectToDeviceAsync(brace);
                    await adapter.StopScanningForDevicesAsync();

                    App.ConnectedDevice = brace.Name;
                    App.DeviceID = brace.Id.ToString();
                    App.RSSI = brace.Rssi.ToString();

                    App.isConnected = true;
                }
                else
                {
                    App.isConnected = false;
                    write("Brace+ not found.", info);
                    return;
                }

                service = await brace.GetServiceAsync(serviceGUID);

                if (service != null)
                {
                    Debug.WriteLine("Connected, scan for devices stopped.");
                    try
                    {
                        // Register characteristics
                        menuCharacteristic = await service.GetCharacteristicAsync(menuGUID);
                        menuCharacteristic.ValueUpdated += async (o, args) =>
                        {
                            var input = Encoding.ASCII.GetString(args.Characteristic.Value);
                            var msg = handler.translate(input, STATUS);
                            Debug.WriteLine(msg);

                            if (input == "^")
                            {
                                switch (STATUS)
                                {
                                    // Do action according to current status of system...
                                    case Constants.SYS_INIT:
                                        write(msg, info);
                                        break;
                                    default:
                                        break;
                                }
                            }
                        };
                        await RUN_BLE_START_UPDATES(menuCharacteristic);

                        streamCharacteristic = await service.GetCharacteristicAsync(streamGUID);
                        streamCharacteristic.ValueUpdated += async (o, args) =>
                        {
                            // extract data
                            var bytes = args.Characteristic.Value;

                            // add to local array
                            bytes.CopyTo(buffer, packetIndex);
                            packetIndex += bytes.Length;

                            // check for packet footer
                            if ((bytes[0] == 'X')&&
                                (bytes[1] == 'Y')&&
                                (bytes[2] == 'Z')&&
                                (STATUS == Constants.SYS_STREAM))
                            {
                                buffer = RELEASE_DATA(buffer);
                                while (!await RUN_BLE_WRITE(menuCharacteristic, "S")) { }
                            }
                        };

                        // Begin system
                        await SystemInit();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Unable to register characteristics: " + ex.Message);
                        return;
                    }

                }
            }
            catch (DeviceConnectionException e)
            {
                Debug.WriteLine("Connection failed with exception: " + e.Message);
                write("Failed to connect.", info);
                return;
            }
            catch (Exception e)
            {
                Debug.WriteLine("Connection failed with exception: " + e.Message);
                write("Failed to connect.", info);
                return;
            }
        }

        public async Task Disconnect()
        {
            isSaving = false;
            isStreaming = false;

            // Send command to put Brace in disconnected state;
            commsByte = Encoding.ASCII.GetBytes(".");
            await RUN_BLE_WRITE(menuCharacteristic, commsByte);
            await RUN_BLE_STOP_UPDATES(menuCharacteristic);

            // Remove all connections
            foreach (IDevice device in adapter.ConnectedDevices)
            {
                await adapter.DisconnectDeviceAsync(device);
            }

            App.ConnectedDevice = "-";
            App.DeviceID = "-";
            App.RSSI = "-";

            App.isConnected = false;
        }

        public async Task StartScan()
        {
            if (!ble.IsOn)
            {
                await Application.Current.MainPage.DisplayAlert("Bluetooth turned off", "Please turn on bluetooth to scan for devices.", "OK");
                return;
            }

            // If no devices found after timeout, stop scan.
            write("Starting scan...", info);
            await adapter.StartScanningForDevicesAsync();

            await Task.Delay(Constants.BLE_SCAN_TIMEOUT_MS);

            if (!App.isConnected)
            {
                await Application.Current.MainPage.DisplayAlert(Constants.DEV_NAME + " not found.", "Unable to find " + Constants.DEV_NAME, "OK");
                await StopScan();
            }
        }
        public async Task StopScan()
        {
            Debug.WriteLine("Stopping scan.");
            await adapter.StopScanningForDevicesAsync();
        }

        public async Task Stream()
        {
            if (App.isConnected)
            {
                if (isStreaming)
                {
                    // Stop stream from menu (any character apart from "S")
                    await RUN_BLE_WRITE(menuCharacteristic, ".");
                    // Stop data stream
                    await RUN_BLE_STOP_UPDATES(streamCharacteristic);
                    write("Stopping data stream.", info);
                    isStreaming = false;
                }
                else
                {
                    // Start data strean.
                    write("Starting data stream...", debug);
                    // Stream data wirelessly
                    if (streamCharacteristic == null)
                    {
                        Debug.WriteLine("Stream characteristic null, quitting.");
                        return;
                    }
                    isStreaming = true;
                    STATUS = Constants.SYS_STREAM;
                    // Start characteristic updates
                    await RUN_BLE_START_UPDATES(streamCharacteristic);
                    // Request stream from menu
                    await RUN_BLE_WRITE(menuCharacteristic, "S");
                }
            }
        }

        public async Task Save()
        {
            if (App.isConnected)
            {
                if (isSaving)
                {
                    saveButtonText = "Log To SD Card";
                    // Stop saving data
                    isSaving = false;
                    STATUS = Constants.IDLE;

                    await PutToIdleState();

                    write("SD Card Logging Finished.", info);
                }
                else
                {
                    saveButtonText = "Stop Saving";

                    // Save data to SD card on Teensy board
                    isSaving = true;
                    STATUS = Constants.LOGGING;

                    //await SaveToSD();
                }
            }
        }

        public async Task TestLogging()
        {
            STATUS = Constants.LOG_TEST;
            //await TestSDDataLog();
        }
        public async Task GetSDInfo()
        {
            STATUS = Constants.SD_TEST;
            //await GetSDCardStatus();
        }
        #endregion

        #region Model Backend 
        private async Task SystemInit()
        {
            // Send init command
            STATUS = Constants.SYS_INIT;
            byte[] bytes = Encoding.ASCII.GetBytes("I");
            await RUN_BLE_WRITE(menuCharacteristic, bytes);
            Debug.WriteLine("Written sys init bytes.");
        }

        private async Task PutToIdleState()
        {
            await RUN_BLE_WRITE(menuCharacteristic, "^");
        }
        #endregion

        byte[] RELEASE_DATA(byte[] bytes)
        {
            // Reset packet index
            packetIndex = 0;
            // Save data
            App.InputData.Add(bytes);
            // Return empty array of same size
            return new byte[bytes.Length];
        }

        async Task<bool> RUN_BLE_WRITE(ICharacteristic c, byte[] b)
        {
            try
            {
                await c.WriteAsync(b);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Characteristic {c.Uuid} write failed with exception: {ex.Message}");
            }
            return false;
        }

        async Task<bool> RUN_BLE_WRITE(ICharacteristic c, string s)
        {
            var b = Encoding.ASCII.GetBytes(s);

            try
            {
                await c.WriteAsync(b);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Characteristic {c.Uuid} write failed with exception: {ex.Message}");
            }
            return false;
        }


        async Task RUN_BLE_START_UPDATES(ICharacteristic c)
        {
            try
            {
                await c.StartUpdatesAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Characteristic {c.Uuid} start updates failed with exception: {ex.Message}");
            }
        }

        async Task RUN_BLE_STOP_UPDATES(ICharacteristic c)
        {
            try
            {
                await c.StopUpdatesAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Characteristic {c.Uuid} stop updates failed with exception: {ex.Message}");
            }
        }

        public void write(string text, Color color)
        {
            Device.BeginInvokeOnMainThread(() =>
            {
                stack.Children.Insert(0, new Label
                {
                    Text = text,
                    TextColor = color
                });

                if (stack.Children.Count > 200)
                {
                    stack.Children.RemoveAt(200);
                }
            });
        }

        void print(string text, Color color)
        {
            Device.BeginInvokeOnMainThread(() =>
            {
                stack.Children.RemoveAt(0);
                stack.Children.Insert(0, new Label
                {
                    Text = text,
                    TextColor = color
                });
            });
        }

        public void clear_messages()
        {
            Device.BeginInvokeOnMainThread(() =>
            {
                stack.Children.Clear();
            });
        }
    }
}
