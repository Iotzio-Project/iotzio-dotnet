# Iotzio

The Iotzio API allows interaction with Iotzio devices. An Iotzio device is a USB connected microchip that enables the host computer to directly control peripherals such as GPIOs, utilize PWM, use I2C, SPI, Onewire and other bus protocols and devices that are not typically available to an application developer on a standard computer. This API is also available to many other programming languages. No extra drivers required!

## Features

- Control GPIOs, utilize PWM, use I2C, SPI, Onewire and other bus protocols
- Direct interaction for various peripherals
- Available for multiple programming languages
- The Iotzio API in its core is using pure idiomatic Rust - blazingly fast and memory safe

## Compatibility

The Iotzio board is compatible with the following platforms:
- Windows
- Linux
- macOS
- Android
- WebAssembly

## Installation

Install `Iotzio` package using `nuget`.

## Usage
Here is a simple example of how to use the Iotzio nuget package:
```
using Com.Iotzio.Api;

var iotzioManager = new IotzioManager();
var iotzioInfos = iotzioManager.ListConnectedBoards();

foreach (var iotzioInfo in iotzioInfos)
{
    using var iotzio = iotzioInfo.Open();

    Console.WriteLine($"Found Iotzio {iotzio.Version()} with serial number {iotzio.SerialNumber()}!");
}
```

Further examples are located in the [examples folder](https://github.com/Iotzio-Project/iotzio-dotnet/tree/main/examples).

## Notes

- On Linux, it is necessary to grant read and write permissions for the Iotzio device:

    ```sh
    sudo usermod -a -G dialout YOUR_USERNAME
    ```

    ```sh
    echo 'KERNEL=="hidraw*", SUBSYSTEM=="hidraw", ATTRS{idVendor}=="2e8a", ATTRS{idProduct}=="000f", GROUP="dialout", MODE="0660"' | sudo tee /etc/udev/rules.d/99-iotzio.rules
    ```

    ```sh
    echo 'SUBSYSTEM=="usb", ATTRS{idVendor}=="2e8a", ATTRS{idProduct}=="000f", GROUP="dialout", MODE="0660"' | sudo tee -a /etc/udev/rules.d/99-iotzio.rules
    ```

    ```sh
    sudo udevadm control --reload-rules
    ```

    ```sh
    sudo udevadm trigger
    ```
