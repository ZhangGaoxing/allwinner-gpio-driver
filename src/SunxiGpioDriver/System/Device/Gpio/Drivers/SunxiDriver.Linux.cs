﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.Device.Gpio.Drivers
{
    public unsafe partial class SunxiDriver : GpioDriver
    {
        private IntPtr gpioPointer0;
        private IntPtr gpioPointer1;
        private const int GpioRegisterOffset0 = 0x01C20800;
        private const int GpioRegisterOffset1 = 0x01F02C00;
        private static readonly object s_initializationLock = new object();
        private static readonly object s_sysFsInitializationLock = new object();
        private const string GpioMemoryFilePath = "/dev/mem";
        private UnixDriver _sysFSDriver = null;
        private readonly IDictionary<int, PinMode> _sysFSModes = new Dictionary<int, PinMode>();

        protected override void Dispose(bool disposing)
        {
            if (gpioPointer0 != default)
            {
                Interop.munmap(gpioPointer0, 0);
                gpioPointer0 = default;
            }

            if (gpioPointer1 != default)
            {
                Interop.munmap(gpioPointer1, 0);
                gpioPointer1 = default;
            }

            if (_sysFSDriver != null)
            {
                _sysFSDriver.Dispose();
                _sysFSDriver = null;
            }
        }

        /// <summary>
        /// Gets the mode of a pin for Unix.
        /// </summary>
        /// <param name="mode">The mode of a pin to get.</param>
        /// <returns>The mode of a pin for Unix.</returns>
        private PinMode GetModeForUnixDriver(PinMode mode)
        {
            switch (mode)
            {
                case PinMode.Input:
                case PinMode.InputPullUp:
                case PinMode.InputPullDown:
                    return PinMode.Input;
                case PinMode.Output:
                    return PinMode.Output;
                default:
                    throw new InvalidOperationException($"Can not parse pin mode {_sysFSModes}");
            }
        }

        /// <summary>
        /// Adds a handler for a pin value changed event.
        /// </summary>
        /// <param name="pinNumber">The pin number in the driver's logical numbering scheme.</param>
        /// <param name="eventTypes">The event types to wait for.</param>
        /// <param name="callback">Delegate that defines the structure for callbacks when a pin value changed event occurs.</param>
        protected internal override void AddCallbackForPinValueChangedEvent(int pinNumber, PinEventTypes eventTypes, PinChangeEventHandler callback)
        {
            ValidatePinNumber(pinNumber);
            InitializeSysFS();

            _sysFSDriver.OpenPin(pinNumber);
            _sysFSDriver.SetPinMode(pinNumber, GetModeForUnixDriver(_sysFSModes[pinNumber]));

            _sysFSDriver.AddCallbackForPinValueChangedEvent(pinNumber, eventTypes, callback);
        }

        /// <summary>
        /// Closes an open pin.
        /// </summary>
        /// <param name="pinNumber">The pin number in the driver's logical numbering scheme.</param>
        protected internal override void ClosePin(int pinNumber)
        {
            ValidatePinNumber(pinNumber);

            if (_sysFSModes.ContainsKey(pinNumber) && _sysFSModes[pinNumber] == PinMode.Output)
            {
                Write(pinNumber, PinValue.Low);
                SetPinMode(pinNumber, PinMode.Input);
            }
        }

        /// <summary>
        /// Checks if a pin supports a specific mode.
        /// </summary>
        /// <param name="pinNumber">The pin number in the driver's logical numbering scheme.</param>
        /// <param name="mode">The mode to check.</param>
        /// <returns>The status if the pin supports the mode.</returns>
        protected internal override bool IsPinModeSupported(int pinNumber, PinMode mode)
        {
            switch (mode)
            {
                case PinMode.Input:
                case PinMode.InputPullDown:
                case PinMode.InputPullUp:
                case PinMode.Output:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Opens a pin in order for it to be ready to use.
        /// </summary>
        /// <param name="pinNumber">The pin number in the driver's logical numbering scheme.</param>
        protected internal override void OpenPin(int pinNumber)
        {
            ValidatePinNumber(pinNumber);
            Initialize();
            SetPinMode(pinNumber, PinMode.Input);
        }

        /// <summary>
        /// Reads the current value of a pin.
        /// </summary>
        /// <param name="pinNumber">The pin number in the driver's logical numbering scheme.</param>
        /// <returns>The value of the pin.</returns>
        protected internal unsafe override PinValue Read(int pinNumber)
        {
            ValidatePinNumber(pinNumber);

            /*
             * There are two registers that contain the value of a pin. Each hold the value of 32
             * different pins. 1 bit represents the value of a pin, 0 is PinValue.Low and 1 is PinValue.High
             */

            uint register = gpioPointer0->GPLEV[pinNumber / 32];
            return Convert.ToBoolean((register >> (pinNumber % 32)) & 1) ? PinValue.High : PinValue.Low;
        }

        /// <summary>
        /// Removes a handler for a pin value changed event.
        /// </summary>
        /// <param name="pinNumber">The pin number in the driver's logical numbering scheme.</param>
        /// <param name="callback">Delegate that defines the structure for callbacks when a pin value changed event occurs.</param>
        protected internal override void RemoveCallbackForPinValueChangedEvent(int pinNumber, PinChangeEventHandler callback)
        {
            ValidatePinNumber(pinNumber);
            InitializeSysFS();

            _sysFSDriver.OpenPin(pinNumber);
            _sysFSDriver.SetPinMode(pinNumber, GetModeForUnixDriver(_sysFSModes[pinNumber]));

            _sysFSDriver.RemoveCallbackForPinValueChangedEvent(pinNumber, callback);
        }

        /// <summary>
        /// Sets the mode to a pin.
        /// </summary>
        /// <param name="pinNumber">The pin number in the driver's logical numbering scheme.</param>
        /// <param name="mode">The mode to be set.</param>
        protected internal override void SetPinMode(int pinNumber, PinMode mode)
        {
            ValidatePinNumber(pinNumber);

            if (!IsPinModeSupported(pinNumber, mode))
            {
                throw new InvalidOperationException($"The pin {pinNumber} does not support the selected mode {mode}.");
            }

            /*
             * There are 6 registers(4-byte ints) that control the mode for all pins. Each
             * register controls the mode for 10 pins. Each pin uses 3 bits in the register
             * containing the mode.
             */

            // Define the shift to get the right 3 bits in the register
            int shift = (pinNumber % 10) * 3;
            // Gets a pointer to the register that holds the mode for the pin
            uint* registerPointer = &gpioPointer0->GPFSEL[pinNumber / 10];
            uint register = *registerPointer;
            // Clear the 3 bits to 0 for the pin Number.
            register &= ~(0b111U << shift);
            // Set the 3 bits to the desired mode for that pin.
            register |= (mode == PinMode.Output ? 1u : 0u) << shift;
            *registerPointer = register;

            if (_sysFSModes.ContainsKey(pinNumber))
            {
                _sysFSModes[pinNumber] = mode;
            }
            else
            {
                _sysFSModes.Add(pinNumber, mode);
            }

            if (mode != PinMode.Output)
            {
                SetInputPullMode(pinNumber, mode);
            }
        }

        /// <summary>
        /// Sets the resistor pull up/down mode for an input pin.
        /// </summary>
        /// <param name="pinNumber">The pin number in the driver's logical numbering scheme.</param>
        /// <param name="mode">The mode of a pin to set the resistor pull up/down mode.</param>
        private void SetInputPullMode(int pinNumber, PinMode mode)
        {
            byte modeToPullMode = mode switch
            {
                PinMode.Input => (byte)0,
                PinMode.InputPullDown => (byte)1,
                PinMode.InputPullUp => (byte)2,
                _ => throw new ArgumentException($"{mode} is not supported as a pull up/down mode.")
            };

            /*
             * This is the process outlined by the BCM2835 datasheet on how to set the pull mode.
             * The GPIO Pull - up/down Clock Registers control the actuation of internal pull-downs on the respective GPIO pins.
             * These registers must be used in conjunction with the GPPUD register to effect GPIO Pull-up/down changes.
             * The following sequence of events is required:
             *
             * 1. Write to GPPUD to set the required control signal (i.e.Pull-up or Pull-Down or neither to remove the current Pull-up/down)
             * 2. Wait 150 cycles – this provides the required set-up time for the control signal
             * 3. Write to GPPUDCLK0/1 to clock the control signal into the GPIO pads you wish to modify
             *    – NOTE only the pads which receive a clock will be modified, all others will retain their previous state.
             * 4. Wait 150 cycles – this provides the required hold time for the control signal
             * 5. Write to GPPUD to remove the control signal
             * 6. Write to GPPUDCLK0/1 to remove the clock
             */

            uint* gppudPointer = &gpioPointer0->GPPUD;
            uint register = *gppudPointer;
            register &= ~0b11U;
            register |= modeToPullMode;
            *gppudPointer = register;

            // Wait 150 cycles – this provides the required set-up time for the control signal
            Thread.SpinWait(150);

            int index = pinNumber / 32;
            int shift = pinNumber % 32;
            uint* gppudclkPointer = &gpioPointer0->GPPUDCLK[index];
            register = *gppudclkPointer;
            register |= 1U << shift;
            *gppudclkPointer = register;

            // Wait 150 cycles – this provides the required hold time for the control signal
            Thread.SpinWait(150);

            register = *gppudPointer;
            register &= ~0b11U;
            *gppudPointer = register;
            *gppudclkPointer = 0;
        }

        /// <summary>
        /// Blocks execution until an event of type eventType is received or a cancellation is requested.
        /// </summary>
        /// <param name="pinNumber">The pin number in the driver's logical numbering scheme.</param>
        /// <param name="eventTypes">The event types to wait for.</param>
        /// <param name="cancellationToken">The cancellation token of when the operation should stop waiting for an event.</param>
        /// <returns>A structure that contains the result of the waiting operation.</returns>
        protected internal override WaitForEventResult WaitForEvent(int pinNumber, PinEventTypes eventTypes, CancellationToken cancellationToken)
        {
            ValidatePinNumber(pinNumber);
            InitializeSysFS();

            _sysFSDriver.OpenPin(pinNumber);
            _sysFSDriver.SetPinMode(pinNumber, GetModeForUnixDriver(_sysFSModes[pinNumber]));

            return _sysFSDriver.WaitForEvent(pinNumber, eventTypes, cancellationToken);
        }

        /// <summary>
        /// Async call until an event of type eventType is received or a cancellation is requested.
        /// </summary>
        /// <param name="pinNumber">The pin number in the driver's logical numbering scheme.</param>
        /// <param name="eventTypes">The event types to wait for.</param>
        /// <param name="cancellationToken">The cancellation token of when the operation should stop waiting for an event.</param>
        /// <returns>A task representing the operation of getting the structure that contains the result of the waiting operation</returns>
        protected internal override ValueTask<WaitForEventResult> WaitForEventAsync(int pinNumber, PinEventTypes eventTypes, CancellationToken cancellationToken)
        {
            ValidatePinNumber(pinNumber);
            InitializeSysFS();

            _sysFSDriver.OpenPin(pinNumber);
            _sysFSDriver.SetPinMode(pinNumber, GetModeForUnixDriver(_sysFSModes[pinNumber]));

            return _sysFSDriver.WaitForEventAsync(pinNumber, eventTypes, cancellationToken);
        }

        /// <summary>
        /// Writes a value to a pin.
        /// </summary>
        /// <param name="pinNumber">The pin number in the driver's logical numbering scheme.</param>
        /// <param name="value">The value to be written to the pin.</param>
        protected internal override void Write(int pinNumber, PinValue value)
        {
            ValidatePinNumber(pinNumber);

            /*
             * If the value is High, GPSET register is used. Otherwise, GPCLR will be used. For
             * both cases, a 1 is set on the corresponding bit in the register in order to set
             * the desired value.
             */

            uint* registerPointer = (value == PinValue.High) ? &gpioPointer0->GPSET[pinNumber / 32] : &gpioPointer0->GPCLR[pinNumber / 32];
            uint register = *registerPointer;
            register = 1U << (pinNumber % 32);
            *registerPointer = register;
        }

        protected ulong SetRegister
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return *(ulong*)(gpioPointer0->GPSET); }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set { *(ulong*)(gpioPointer0->GPSET) = value; }
        }

        protected ulong ClearRegister
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return *(ulong*)(gpioPointer0->GPCLR); }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set { *(ulong*)(gpioPointer0->GPCLR) = value; }
        }

        private void InitializeSysFS()
        {
            if (_sysFSDriver != null)
            {
                return;
            }
            lock (s_sysFsInitializationLock)
            {
                if (_sysFSDriver != null)
                {
                    return;
                }
                _sysFSDriver = new SysFsDriver();
            }
        }

        private void Initialize()
        {
            if (gpioPointer0 != null)
            {
                return;
            }

            lock (s_initializationLock)
            {
                if (gpioPointer0 != null)
                {
                    return;
                }

                int fileDescriptor = Interop.open(GpioMemoryFilePath, FileOpenFlags.O_RDWR | FileOpenFlags.O_SYNC);
                if (fileDescriptor == -1)
                {
                    throw new IOException($"Error {Marshal.GetLastWin32Error()} initializing the Gpio driver.");
                }

                IntPtr mapPointer0 = Interop.mmap(IntPtr.Zero, Environment.SystemPageSize, (MemoryMappedProtections.PROT_READ | MemoryMappedProtections.PROT_WRITE), MemoryMappedFlags.MAP_SHARED, fileDescriptor, GpioRegisterOffset0);
                IntPtr mapPointer1 = Interop.mmap(IntPtr.Zero, Environment.SystemPageSize, (MemoryMappedProtections.PROT_READ | MemoryMappedProtections.PROT_WRITE), MemoryMappedFlags.MAP_SHARED, fileDescriptor, GpioRegisterOffset1);
                if (mapPointer0.ToInt64() == -1 || mapPointer1.ToInt64() == -1)
                {
                    throw new IOException($"Error {Marshal.GetLastWin32Error()} initializing the Gpio driver.");
                }

                Interop.close(fileDescriptor);

                gpioPointer0 = mapPointer0;
                gpioPointer1 = mapPointer1;
            }
        }

        /// <summary>
        /// Gets the mode of a pin.
        /// </summary>
        /// <param name="pinNumber">The pin number in the driver's logical numbering scheme.</param>
        /// <returns>The mode of the pin.</returns>
        protected internal override PinMode GetPinMode(int pinNumber)
        {
            ValidatePinNumber(pinNumber);

            if (!_sysFSModes.ContainsKey(pinNumber))
            {
                throw new InvalidOperationException("Can not get a pin mode of a pin that is not open.");
            }
            return _sysFSModes[pinNumber];
        }
    }
}
