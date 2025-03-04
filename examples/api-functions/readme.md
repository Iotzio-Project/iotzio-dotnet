# API Functions Example

This solution contains various examples that can be run on a computer. Each example is a separate project.

## Running the Examples

To run the desired example using Visual Studio:

1. Open the solution in Visual Studio.

2. In the Solution Explorer, navigate to the project you want to run.

3. Right-click on the project and select "Set as Startup Project".

4. Press F5 or click on the "Start" button to run the project.

## Requirements

- .NET SDK 8.0 installed

- Visual Studio 2022 (version 17.3 or later) installed with the .NET desktop development workload

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