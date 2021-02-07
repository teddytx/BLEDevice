using System;
using System.Threading.Tasks;
using System.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Windows.System.Threading;
using Windows.ApplicationModel.Background;
using System.Collections.Generic;

namespace SDKTemplate
{
    // This scenario connects to the device selected in the "Discover
    // GATT Servers" scenario and communicates with it.
    // Note that this scenario is rather artificial because it communicates
    // with an unknown service with unknown characteristics.
    // In practice, your app will be interested in a specific service with
    // a specific characteristic.
    public sealed partial class Scenario2_Client : Page
    {
        #region Variables
        private MainPage rootPage = MainPage.Current;

        private BluetoothLEDevice bluetoothLeDevice = null;
        private GattDeviceService selectedService; 
        private GattCharacteristic selectedCharacteristic;

        // Only one registered characteristic at a time.
        private GattCharacteristic registeredCharacteristic;

        private bool subscribedForNotifications = false;

        // Variables for display and output file
        private string messageHR;
        private string messageOX;

        private ThreadPoolTimer PeriodicTimer;
        static SemaphoreSlim LogfileSemaphore = new SemaphoreSlim(1, 1);

        #endregion

        #region Error Codes
        //readonly int E_BLUETOOTH_ATT_WRITE_NOT_PERMITTED = unchecked((int)0x80650003);
        //readonly int E_BLUETOOTH_ATT_INVALID_PDU = unchecked((int)0x80650004);
        //readonly int E_ACCESSDENIED = unchecked((int)0x80070005);
        readonly int E_DEVICE_NOT_AVAILABLE = unchecked((int)0x800710df); // HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_AVAILABLE)
        #endregion

        #region UI Code
        public Scenario2_Client() => InitializeComponent();

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            SelectedDeviceRun.Text = rootPage.SelectedBleDeviceName;
            if (string.IsNullOrEmpty(rootPage.SelectedBleDeviceId))
            {
                ConnectButton.IsEnabled = false;
            }
            if (!rootPage.SelectedBleDeviceName.ToUpper().Contains("NONIN"))
            {
                ConnectButton.IsEnabled = false;
            }
        }

        protected override async void OnNavigatedFrom(NavigationEventArgs e)
        {
            if (!await ClearBluetoothLEDeviceAsync())
            {
                rootPage.NotifyUser("Error: Unable to reset app state", NotifyType.ErrorMessage);
            }
        }

        /// <summary>
        /// When the CONNECT button is clicked, the program selects service and characteristic and establishes connection.
        /// </summary>
        private async void ConnectButton_Click()
        {
            ConnectButton.IsEnabled = false;

            if (!await ClearBluetoothLEDeviceAsync())
            {
                rootPage.NotifyUser("Error: Unable to reset state, try again.", NotifyType.ErrorMessage);
                ConnectButton.IsEnabled = true;
                return;
            }

            try
            {
                // BT_Code: BluetoothLEDevice.FromIdAsync must be called from a UI thread because it may prompt for consent.
                bluetoothLeDevice = await BluetoothLEDevice.FromIdAsync(rootPage.SelectedBleDeviceId);

                if (bluetoothLeDevice == null)
                {
                    rootPage.NotifyUser("Failed to connect to device.", NotifyType.ErrorMessage);
                    ConnectButton.IsEnabled = true;
                    return;
                }
            }
            catch (Exception ex) when (ex.HResult == E_DEVICE_NOT_AVAILABLE)
            {
                rootPage.NotifyUser("Bluetooth radio is not on.", NotifyType.ErrorMessage);
                ConnectButton.IsEnabled = true;
                return;
            }

            // When device is selected, clear subscriptions if any
            RemoveValueChangedHandler();

            if (bluetoothLeDevice == null)
            {
                ConnectButton.IsEnabled = true;
                return;
            }

            // Note: BluetoothLEDevice.GattServices property will return an empty list for unpaired devices. For all uses we recommend using the GetGattServicesAsync method.
            // BT_Code: GetGattServicesAsync returns a list of all the supported services of the device (even if it's not paired to the system).
            // If the services supported by the device are expected to change during BT usage, subscribe to the GattServicesChanged event.
            GattDeviceServicesResult serviceResult = await bluetoothLeDevice.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            // If successfully connect the service
            if (serviceResult.Status != GattCommunicationStatus.Success)
            {
                rootPage.NotifyUser("Device unreachable", NotifyType.ErrorMessage);
                ConnectButton.IsEnabled = true;
                return;
            }

            var services = serviceResult.Services;
            rootPage.NotifyUser($"Found {services.Count} services", NotifyType.StatusMessage);

            var selectedService = SelectService(services);

            // Find characteristics and subscribe
            if (selectedService != null)
            {
                await SearchCharacteristicAsync("Oximetry");
                SubscribeWithCharacteristicAsync();
            }
        }

        #endregion

        #region Enumerating Services

        /// <summary>
        /// Clear the subscription of a device being subscribed
        /// </summary>
        /// <returns></returns>
        private async Task<bool> ClearBluetoothLEDeviceAsync()
        {
            // If a device is being subscribed..
            if (subscribedForNotifications)
            {
                // Need to clear the CCCD from the remote device so we stop receiving notifications
                if (await registeredCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None)
                    != GattCommunicationStatus.Success)
                    return false;
                selectedCharacteristic.ValueChanged -= Characteristic_ValueChanged;
                subscribedForNotifications = false;
            }
            bluetoothLeDevice?.Dispose();
            bluetoothLeDevice = null;
            return true;
        }

        /// <summary>
        /// Select NONIN Customed Oximetry Service
        /// </summary>
        /// <param name="services"></param>
        /// <returns></returns>
        private GattDeviceService SelectService(System.Collections.Generic.IReadOnlyList<GattDeviceService> services)
        {
            foreach (var service in services)
            {
                // If target service is found, exit the loop
                if (DisplayHelpers.GetServiceName(service) == "Custom Service: 46a970e0-0d5f-11e2-8b5e-0002a5d5c51b")
                {
                    // select the service
                    return selectedService = service;
                }
            }
            return null;
        }
       

        /// <summary>
        /// Search for the target characteristic after the serivce has been found
        /// </summary>
        /// <param name="service"></param>
        /// <param name="characteristicKeyword"></param>
        private async Task SearchCharacteristicAsync(String characteristicKeyword)
        {
            // try to connect the service and characteristics
            try
            {
                if (await selectedService.RequestAccessAsync() == DeviceAccessStatus.Allowed)
                {
                    // BT_Code: Get all the child characteristics of a service. Use the cache mode to specify uncached characterstics only 
                    // and the new Async functions to get the characteristics of unpaired devices as well. 
                    var result = await selectedService.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                    if (result.Status == GattCommunicationStatus.Success)
                    {
                        foreach (GattCharacteristic c in result.Characteristics)
                        {
                            if (DisplayHelpers.GetCharacteristicName(c).Contains(characteristicKeyword))
                            {
                                selectedCharacteristic = c;
                                rootPage.NotifyUser("Accessing service...", NotifyType.StatusMessage);
                                ConnectButton.IsEnabled = false;
                                break;
                            }
                        }

                        // If the characteristic is found:
                        // Get all the child descriptors of the characteristic. Use the cache mode to specify uncached descriptors only 
                        // and the new Async functions to get the descriptors of unpaired devices as well. 
                        var descriptorResult = await selectedCharacteristic.GetDescriptorsAsync(BluetoothCacheMode.Uncached);
                        if (descriptorResult.Status != GattCommunicationStatus.Success) // how to constantly check for connection?
                        {
                            rootPage.NotifyUser($"Descriptor read failure: {descriptorResult.Status}", NotifyType.ErrorMessage);
                            ConnectButton.IsEnabled = true;
                            return;
                        }
                    }
                    else
                    {
                        rootPage.NotifyUser($"Error accessing service [{DisplayHelpers.GetServiceName(selectedService)}].", NotifyType.ErrorMessage);
                        ConnectButton.IsEnabled = true;
                        // On error, act as if there are no characteristics.
                    }
                }
                else
                {
                    // Not granted access
                    rootPage.NotifyUser($"Error accessing service [{DisplayHelpers.GetServiceName(selectedService)}].", NotifyType.ErrorMessage);
                    ConnectButton.IsEnabled = true;
                }

            }
            catch (Exception ex)
            {
                rootPage.NotifyUser("Restricted service. Can't read characteristics: " + ex.Message,
                    NotifyType.ErrorMessage);
                // On error, act as if there are no characteristics.
                ConnectButton.IsEnabled = true;
                return;
            }

        }

        /// <summary>
        /// Subscribe after the characteristic has been found
        /// </summary>
        private async void SubscribeWithCharacteristicAsync()
        {
            if (!subscribedForNotifications)
            {
                // initialize status
                GattCommunicationStatus status = GattCommunicationStatus.Unreachable;
                var cccdValue = GattClientCharacteristicConfigurationDescriptorValue.None;
                if (selectedCharacteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
                    cccdValue = GattClientCharacteristicConfigurationDescriptorValue.Indicate;
                else if (selectedCharacteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
                    cccdValue = GattClientCharacteristicConfigurationDescriptorValue.Notify;

                try
                {
                    // BT_Code: Must write the CCCD in order for server to send indications.
                    // We receive them in the ValueChanged event handler.
                    status = await selectedCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(cccdValue);

                    if (status == GattCommunicationStatus.Success)
                    {
                        AddValueChangedHandler();
                        rootPage.NotifyUser("Successfully subscribed for value changes", NotifyType.StatusMessage);
                    }
                    else
                        rootPage.NotifyUser($"Error registering for value changes: {status}", NotifyType.ErrorMessage);
                }
                catch (UnauthorizedAccessException ex)
                {
                    // This usually happens when a device reports that it support indicate, but it actually doesn't.
                    rootPage.NotifyUser(ex.Message, NotifyType.ErrorMessage);
                }
            }
            else 
            {
                try
                {
                    // BT_Code: Must write the CCCD in order for server to send notifications.
                    // We receive them in the ValueChanged event handler.
                    // Note that this sample configures either Indicate or Notify, but not both.
                    var result = await
                            selectedCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                                GattClientCharacteristicConfigurationDescriptorValue.None);
                    if (result == GattCommunicationStatus.Success)
                    {
                        subscribedForNotifications = true;
                        RemoveValueChangedHandler();
                        rootPage.NotifyUser("Successfully un-registered for notifications", NotifyType.StatusMessage);
                    }
                    else
                    {
                        rootPage.NotifyUser($"Error un-registering for notifications: {result}", NotifyType.ErrorMessage);
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    // This usually happens when a device reports that it support notify, but it actually doesn't.
                    rootPage.NotifyUser(ex.Message, NotifyType.ErrorMessage);
                }
            }
        }



        /// <summary>
        /// To be edited
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private void Characteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            // BT_Code: An Indicate or Notify reported that the value has changed.
            // Display the new value with a timestamp.
            var newValue = FormatValueByPresentation(args.CharacteristicValue);
            messageHR = newValue[0];
            messageOX = newValue[1];
 
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                () =>
                {
                    TimeValue.Text = $"{DateTime.Now:hh:mm:ss}";
                    HeartRateLatestValue.Text = messageHR;
                    OximetryLatestValue.Text = messageOX;
                });
        }


        private void RemoveValueChangedHandler()
        {
            if (subscribedForNotifications)
            {
                registeredCharacteristic.ValueChanged -= Characteristic_ValueChanged;
                registeredCharacteristic = null;
                subscribedForNotifications = false;
            }
        }

        private void AddValueChangedHandler()
        {
            if (!subscribedForNotifications)
            {
                registeredCharacteristic = selectedCharacteristic;
                registeredCharacteristic.ValueChanged += Characteristic_ValueChanged;
                subscribedForNotifications = true;
            }
        }

        
        /// <summary>
        /// To be edited
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="format"></param>
        /// <returns></returns>
        private string[] FormatValueByPresentation(IBuffer buffer)
        {
            // BT_Code: Byte 7 is SpO2 (0-100), displayed as 4-beat avg; Byte 8-9 is heart rate (0-321),
            // displayed in 4-beat avg
            byte[] data;
            string[] conversionResult = new String[2];
            CryptographicBuffer.CopyToByteArray(buffer, out data);
            if (data!= null)
            {
                try
                {
                    conversionResult[0] = BitConverter.ToInt16(new byte[] { data[9], data[8] }, 0).ToString();
                     
                    conversionResult[1] = data[7].ToString(); //BitConverter.ToInt8(data, 7).ToString();

                    // If NONIN'S designated codes for disconnection are detected, convert to display/write file
                    if (conversionResult[0] == "511") // Heart rate
                        conversionResult[0] = "0";
                    if (conversionResult[1] == "127") // Sp02
                        conversionResult[1] = "0";
                }
                catch (ArgumentException)
                {
                    conversionResult[0] = "(Unable to convert heart rate)";
                    conversionResult[1] = "(Unable to convert oximetry)";
                }
            }
            else
            {
                conversionResult[0] = "(Cannot read heart rate)";
                conversionResult[1] = "(Cannot read oximetry)";
            }

            if (conversionResult[0] == "0" | conversionResult[1] == "0")
            {
                rootPage.NotifyUser("Please check connection and reconnect.", NotifyType.ErrorMessage);
                RemoveValueChangedHandler();
            }
            return conversionResult;
        }

        #endregion



        #region Record Data
        /// <summary>
        /// When the "Record" button is clicked, the SpO2 and Heart rate data read from the BLE device are written to file
        /// "NN3150.txt" at the frequency every 1 second.
        /// </summary>
        private void RecordButton_Click()
        {
            if (RecordButton.Content.ToString() == "Record")
            {
                RecordButton.Content = "Stop";
                WriteFile();
            }
            else
            {
                if (PeriodicTimer != null)
                {
                    RecordButton.Content = "Record";
                    PeriodicTimer.Cancel();
                    LogfileSemaphore.Release();
                }
            }
        }


        TimeSpan period = TimeSpan.FromSeconds(1);

        private async void WriteFile()
        {
            var savePicker = new Windows.Storage.Pickers.FileSavePicker();
            savePicker.SuggestedStartLocation =
                Windows.Storage.Pickers.PickerLocationId.Desktop;
            // Dropdown of file types the user can save the file as
            savePicker.FileTypeChoices.Add("Plain Text", new List<string>() { ".txt" });
            // Default file name if the user does not type one in or select a file to replace
            savePicker.SuggestedFileName = "NN3150";
            await LogfileSemaphore.WaitAsync();
            try
            {
                Windows.Storage.StorageFile file = await savePicker.PickSaveFileAsync();
                await Windows.Storage.FileIO.WriteTextAsync(file, messageOX + ";" + messageHR);
                PeriodicTimer = ThreadPoolTimer.CreatePeriodicTimer(async (source) =>
                {
                    await Windows.Storage.FileIO.AppendTextAsync(file, "\n" + messageOX + ";" + messageHR);

                }, period);
            }
            catch (Exception ex)
            {
                rootPage.NotifyUser(ex.Message + " Recording session was interrupted. Please restart.", NotifyType.ErrorMessage);
                RemoveValueChangedHandler();
                RecordButton.Content = "Record";
                PeriodicTimer.Cancel();
                LogfileSemaphore.Release();
            }
        }

        #endregion
    }
}