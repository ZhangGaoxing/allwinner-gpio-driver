﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Device.Gpio;
using System.Device.Gpio.Drivers;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Iot.Device.Gpio.Drivers
{
    /// <summary>
    /// A generic GPIO driver for Allwinner SoCs.
    /// </summary>
    /// <remarks>
    /// This is a generic GPIO driver for Allwinner SoCs.
    /// It can even drive the internal pins that are not drawn out.
    /// Before you operate, you must be clear about what you are doing.
    /// </remarks>
    public unsafe class SunxiDriver : SysFsDriver
    {
        private const string GpioMemoryFilePath = "/dev/mem";
        private IDictionary<int, PinState> _pinModes = new Dictionary<int, PinState>();
        // final_address = mapped_address + (target_address & map_mask) https://stackoverflow.com/a/37922968
        private static readonly int _mapMask = Environment.SystemPageSize - 1;
        private static readonly object s_initializationLock = new object();

        private IntPtr _cpuxPointer = IntPtr.Zero;
        private IntPtr _cpusPointer = IntPtr.Zero;

        /// <summary>
        /// CPUX-PORT base address.
        /// </summary>
        protected virtual int CpuxPortBaseAddress { get; }

        /// <summary>
        /// CPUS-PORT base address.
        /// </summary>
        protected virtual int CpusPortBaseAddress { get; }

        /// <inheritdoc/>
        protected override int PinCount => throw new PlatformNotSupportedException("This driver is generic so it can not enumerate how many pins are available.");

        /// <inheritdoc/>
        protected override int ConvertPinNumberToLogicalNumberingScheme(int pinNumber) => throw new PlatformNotSupportedException("This driver is generic so it can not perform conversions between pin numbering schemes.");

        /// <summary>
        /// Initializes a new instance of the <see cref="SunxiDriver"/> class.
        /// </summary>
        protected SunxiDriver()
        {
            Initialize();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SunxiDriver"/>.
        /// </summary>
        /// <param name="cpuxPortBaseAddress">CPUX-PORT base address (This can be find in the corresponding SoC datasheet).</param>
        /// <param name="cpusPortBaseAddress">CPUS-PORT base address (This can be find in the corresponding SoC datasheet).</param>
        public SunxiDriver(int cpuxPortBaseAddress, int cpusPortBaseAddress)
        {
            CpuxPortBaseAddress = cpuxPortBaseAddress;
            CpusPortBaseAddress = cpusPortBaseAddress;

            Initialize();
        }

        /// <inheritdoc/>
        protected override void OpenPin(int pinNumber)
        {
            SetPinMode(pinNumber, PinMode.Input);
        }

        /// <inheritdoc/>
        protected override void ClosePin(int pinNumber)
        {
            if (_pinModes.ContainsKey(pinNumber))
            {
                if (_pinModes[pinNumber].InUseByInterruptDriver)
                {
                    base.ClosePin(pinNumber);
                }

                switch (_pinModes[pinNumber].CurrentPinMode)
                {
                    case PinMode.InputPullDown:
                    case PinMode.InputPullUp:
                        SetPinMode(pinNumber, PinMode.Input);
                        break;
                    case PinMode.Output:
                        Write(pinNumber, PinValue.Low);
                        SetPinMode(pinNumber, PinMode.Input);
                        break;
                    default:
                        break;
                }

                _pinModes.Remove(pinNumber);
            }
        }

        /// <inheritdoc/>
        protected override void SetPinMode(int pinNumber, PinMode mode)
        {
            // Get port controller, port number and shift
            (int PortController, int Port) unmapped = UnmapPinNumber(pinNumber);
            int cfgNum = unmapped.Port / 8;
            int cfgShift = unmapped.Port % 8;
            int pulNum = unmapped.Port / 16;
            int pulShift = unmapped.Port % 16;

            // Pn_CFG is used to set the direction; Pn_PUL is used to set the pull up/dowm mode
            int cfgOffset, pulOffset;
            // Pn_CFG initial offset is 0x00
            cfgOffset = unmapped.PortController * 0x24 + cfgNum * 0x04;
            // Pn_PUL initial offset is 0x1C
            pulOffset = unmapped.PortController * 0x24 + 0x1C + pulNum * 0x04;

            uint* cfgPointer, pulPointer;

            if (unmapped.PortController < 10)
            {
                cfgPointer = (uint*)(_cpuxPointer + cfgOffset);
                pulPointer = (uint*)(_cpuxPointer + pulOffset);
            }
            else
            {
                cfgPointer = (uint*)(_cpusPointer + cfgOffset);
                pulPointer = (uint*)(_cpusPointer + pulOffset);
            }

            uint cfgValue = *cfgPointer;
            uint pulValue = *pulPointer;

            // Clear register
            // Input is 0b000; Output is 0b001
            cfgValue &= ~(0b1111U << (cfgShift * 4));
            // Pull-up is 0b01; Pull-down is 0b10; Default is 0b00
            pulValue &= ~(0b11U << (pulShift * 2));

            switch (mode)
            {
                case PinMode.Output:
                    cfgValue |= 0b_001U << (cfgShift * 4);
                    break;
                case PinMode.Input:
                    // After clearing the register, the value is the input mode.
                    break;
                case PinMode.InputPullDown:
                    pulValue |= 0b10U << (pulShift * 2);
                    break;
                case PinMode.InputPullUp:
                    pulValue |= 0b01U << (pulShift * 2);
                    break;
                default:
                    throw new ArgumentException("Unsupported pin mode.");
            }

            *cfgPointer = cfgValue;
            Thread.SpinWait(150);
            *pulPointer = pulValue;

            if (_pinModes.ContainsKey(pinNumber))
            {
                _pinModes[pinNumber].CurrentPinMode = mode;
            }
            else
            {
                _pinModes.Add(pinNumber, new PinState(mode));
            }
        }

        /// <inheritdoc/>
        protected override void Write(int pinNumber, PinValue value)
        {
            if (!_pinModes.ContainsKey(pinNumber))
            {
                throw new InvalidOperationException("Can not write a value to a pin that is not open.");
            }
            else
            {
                if (_pinModes[pinNumber].CurrentPinMode != PinMode.Output)
                {
                    throw new InvalidOperationException("Can not write a value to a pin that is not output mode.");
                }
            }

            (int PortController, int Port) unmapped = UnmapPinNumber(pinNumber);

            uint* dataPointer;
            // Pn_DAT offset is 0x10
            int dataOffset = unmapped.PortController * 0x24 + 0x10;

            if (unmapped.PortController < 10)
            {
                dataPointer = (uint*)(_cpuxPointer + dataOffset);
            }
            else
            {
                dataPointer = (uint*)(_cpusPointer + dataOffset);
            }

            uint dataValue = *dataPointer;

            if (value == PinValue.High)
            {
                dataValue |= 0b1U << unmapped.Port;
            }
            else
            {
                dataValue &= ~(0b1U << unmapped.Port);
            }

            *dataPointer = dataValue;
        }

        /// <inheritdoc/>
        protected unsafe override PinValue Read(int pinNumber)
        {
            if (!_pinModes.ContainsKey(pinNumber))
            {
                throw new InvalidOperationException("Can not read a value from a pin that is not open.");
            }
            else
            {
                if (_pinModes[pinNumber].CurrentPinMode == PinMode.Output)
                {
                    throw new InvalidOperationException("Can not read a value from a pin that is output mode.");
                }
            }

            (int PortController, int Port) unmapped = UnmapPinNumber(pinNumber);

            uint* dataPointer;
            // Pn_DAT offset is 0x10
            int dataOffset = unmapped.PortController * 0x24 + 0x10;

            if (unmapped.PortController < 10)
            {
                dataPointer = (uint*)(_cpuxPointer + dataOffset);
            }
            else
            {
                dataPointer = (uint*)(_cpusPointer + dataOffset);
            }

            uint dataValue = *dataPointer;

            return Convert.ToBoolean((dataValue >> unmapped.Port) & 0b1) ? PinValue.High : PinValue.Low;
        }

        /// <inheritdoc/>
        protected override void AddCallbackForPinValueChangedEvent(int pinNumber, PinEventTypes eventTypes, PinChangeEventHandler callback)
        {
            if (!_pinModes.ContainsKey(pinNumber))
            {
                throw new InvalidOperationException("Can not add a handler to a pin that is not open.");
            }
            else
            {
                if (_pinModes[pinNumber].CurrentPinMode == PinMode.Output)
                {
                    throw new InvalidOperationException("Can not add a handler to a pin that is output mode.");
                }
            }

            _pinModes[pinNumber].InUseByInterruptDriver = true;

            base.OpenPin(pinNumber);
            base.AddCallbackForPinValueChangedEvent(pinNumber, eventTypes, callback);
        }

        /// <inheritdoc/>
        protected override void RemoveCallbackForPinValueChangedEvent(int pinNumber, PinChangeEventHandler callback)
        {
            if (!_pinModes.ContainsKey(pinNumber))
            {
                throw new InvalidOperationException("Can not add a handler to a pin that is not open.");
            }

            _pinModes[pinNumber].InUseByInterruptDriver = false;

            base.OpenPin(pinNumber);
            base.RemoveCallbackForPinValueChangedEvent(pinNumber, callback);
        }

        /// <inheritdoc/>
        protected override WaitForEventResult WaitForEvent(int pinNumber, PinEventTypes eventTypes, CancellationToken cancellationToken)
        {
            if (!_pinModes.ContainsKey(pinNumber))
            {
                throw new InvalidOperationException("Can not add a block execution to a pin that is not open.");
            }

            _pinModes[pinNumber].InUseByInterruptDriver = true;

            base.OpenPin(pinNumber);
            return base.WaitForEvent(pinNumber, eventTypes, cancellationToken);
        }

        /// <inheritdoc/>
        protected override ValueTask<WaitForEventResult> WaitForEventAsync(int pinNumber, PinEventTypes eventTypes, CancellationToken cancellationToken)
        {
            if (!_pinModes.ContainsKey(pinNumber))
            {
                throw new InvalidOperationException("Can not async call to a pin that is not open.");
            }

            _pinModes[pinNumber].InUseByInterruptDriver = true;

            base.OpenPin(pinNumber);
            return base.WaitForEventAsync(pinNumber, eventTypes, cancellationToken);
        }

        /// <inheritdoc/>
        protected override bool IsPinModeSupported(int pinNumber, PinMode mode)
        {
            return mode switch
            {
                PinMode.Input or PinMode.InputPullDown or PinMode.InputPullUp or PinMode.Output => true,
                _ => false,
            };
        }

        /// <inheritdoc/>
        protected override PinMode GetPinMode(int pinNumber)
        {
            if (!_pinModes.ContainsKey(pinNumber))
            {
                throw new InvalidOperationException("Can not get a pin mode of a pin that is not open.");
            }

            return _pinModes[pinNumber].CurrentPinMode;
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (_cpuxPointer != IntPtr.Zero)
            {
                Interop.munmap(_cpuxPointer, 0);
                _cpuxPointer = IntPtr.Zero;
            }

            if (_cpusPointer != IntPtr.Zero)
            {
                Interop.munmap(_cpusPointer, 0);
                _cpusPointer = IntPtr.Zero;
            }
        }

        private void Initialize()
        {
            if (_cpuxPointer != IntPtr.Zero)
            {
                return;
            }

            lock (s_initializationLock)
            {
                if (_cpuxPointer != IntPtr.Zero)
                {
                    return;
                }

                int fileDescriptor = Interop.open(GpioMemoryFilePath, FileOpenFlags.O_RDWR | FileOpenFlags.O_SYNC);
                if (fileDescriptor < 0)
                {
                    throw new IOException($"Error {Marshal.GetLastWin32Error()} initializing the Gpio driver (File open error).");
                }

                IntPtr cpuxMap = Interop.mmap(IntPtr.Zero, Environment.SystemPageSize - 1, (MemoryMappedProtections.PROT_READ | MemoryMappedProtections.PROT_WRITE), MemoryMappedFlags.MAP_SHARED, fileDescriptor, CpuxPortBaseAddress & ~_mapMask);
                IntPtr cpusMap = Interop.mmap(IntPtr.Zero, Environment.SystemPageSize, MemoryMappedProtections.PROT_READ | MemoryMappedProtections.PROT_WRITE, MemoryMappedFlags.MAP_SHARED, fileDescriptor, CpusPortBaseAddress & ~_mapMask);

                if (cpuxMap.ToInt64() == -1)
                {
                    Interop.munmap(cpuxMap, 0);
                    throw new IOException($"Error {Marshal.GetLastWin32Error()} initializing the Gpio driver (CPUx initialize error).");
                }

                if (cpusMap.ToInt64() == -1)
                {
                    Interop.munmap(cpusMap, 0);
                    throw new IOException($"Error {Marshal.GetLastWin32Error()} initializing the Gpio driver (CPUs initialize error).");
                }

                Interop.close(fileDescriptor);

                _cpuxPointer = cpuxMap;
                _cpusPointer = cpusMap;
            }
        }

        /// <summary>
        /// Map pin number with port controller name to pin number in the driver's logical numbering scheme.
        /// </summary>
        /// <param name="portController">Port controller name, like 'A', 'C'.</param>
        /// <param name="port">Number of pins.</param>
        /// <returns>Pin number in the driver's logical numbering scheme.</returns>
        public static int MapPinNumber(char portController, int port)
        {
            int alphabetPosition = (portController >= 'A' && portController <= 'Z') ? portController - 'A' : throw new Exception();

            return alphabetPosition * 32 + port;
        }

        protected static (int PortController, int Port) UnmapPinNumber(int pinNumber)
        {
            int port = pinNumber % 32;
            int portController = (pinNumber - port) / 32;

            return (portController, port);
        }

        private class PinState
        {
            public PinState(PinMode currentMode)
            {
                CurrentPinMode = currentMode;
                InUseByInterruptDriver = false;
            }

            public PinMode CurrentPinMode { get; set; }

            public bool InUseByInterruptDriver { get; set; }
        }
    }
}