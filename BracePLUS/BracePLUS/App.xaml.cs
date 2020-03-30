﻿using System;
using Xamarin.Forms;
using BracePLUS.Views;
using System.Diagnostics;
using BracePLUS.Models;
using static BracePLUS.Extensions.Constants;
using System.IO;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Threading.Tasks;
using BracePLUS.Extensions;
using Xamarin.Essentials;

namespace BracePLUS
{
    public partial class App : Application
    {
        // Models
        public static BraceClient Client;
        public static Configuration Config;
        public static MessageHandler handler;

        // Global Members
        public static Random generator;
        public static List<byte[]> InputData;
        public static List<string> MobileFiles;
        public static string FolderPath { get; private set; }

        // BLE Status
        public static string ConnectedDevice
        { 
            get { return Client.Brace.Name; }
            set { } 
        }
        public static string DeviceID
        { 
            get { return Client.Brace.Id.ToString(); }
            set { }
        }
        public static string RSSI 
        { 
            get { return Client.Brace.Rssi.ToString(); }
            set { }
        }

        // User Info
        public static double GlobalMax { get; set; }
        public static double GlobalAverage { get; set; }

        // Global variables
        public static bool isConnected;

        public App()
        {
            //Register Syncfusion license
            Syncfusion.Licensing.SyncfusionLicenseProvider.RegisterLicense(SyncFusionLicense);
            FolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

            //ClearFiles();
            InitializeComponent();

            generator = new Random();
            handler = new MessageHandler();
            InputData = new List<byte[]>();
            MobileFiles = new List<string>();  

            Client = new BraceClient();
            MainPage = new MainPage();
        }

        protected override async void OnStart()
        {
            isConnected = false;
            await Client.StartScan();
        }

        protected override void OnSleep()
        {
            // Handle when your app sleeps
        }

        protected override void OnResume()
        {
            // Handle when your app resumes
        }

        static public void Vibrate(int time)
        {
            try
            {
                var duration = TimeSpan.FromSeconds(time);
                Vibration.Vibrate(duration);
            }
            catch (FeatureNotSupportedException ex)
            {
                Debug.WriteLine("Vibration not supported:" + ex.Message);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Vibration failed: " + ex.Message);
            }
        }

        private void ClearFiles()
        {
            var files = Directory.GetFiles(FolderPath);
            Debug.WriteLine("Found directory files:");
            foreach (var file in files)
                Debug.WriteLine(file);

            foreach (var file in files)
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }

            files = Directory.GetFiles(FolderPath);
            Debug.WriteLine("Files left over:");
            foreach (var file in files)
                Debug.WriteLine(file);
        }
    }
}