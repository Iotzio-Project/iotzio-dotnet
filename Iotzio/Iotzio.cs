using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
namespace Com.Iotzio.Api;



// This is a helper for safely working with byte buffers returned from the Rust code.
// A rust-owned buffer is represented by its capacity, its current length, and a
// pointer to the underlying data.

[StructLayout(LayoutKind.Sequential)]
internal struct RustBuffer
{
  public ulong capacity;
  public ulong len;
  public IntPtr data;

  public static RustBuffer Alloc(int size)
  {
    return _UniffiHelpers.RustCall((ref UniffiRustCallStatus status) => {
      var buffer = _UniFFILib.ffi_iotzio_rustbuffer_alloc(Convert.ToUInt64(size), ref status);
      if (buffer.data == IntPtr.Zero)
      {
        throw new AllocationException($"RustBuffer.Alloc() returned null data pointer (size={size})");
      }
      return buffer;
    });
  }

  public static void Free(RustBuffer buffer)
  {
    _UniffiHelpers.RustCall((ref UniffiRustCallStatus status) => {
      _UniFFILib.ffi_iotzio_rustbuffer_free(buffer, ref status);
    });
  }

  public static BigEndianStream MemoryStream(IntPtr data, long length)
  {
    unsafe
    {
      return new BigEndianStream(new UnmanagedMemoryStream((byte*)data.ToPointer(), length));
    }
  }

  public BigEndianStream AsStream()
  {
    unsafe
    {
      return new BigEndianStream(
          new UnmanagedMemoryStream((byte*)data.ToPointer(), Convert.ToInt64(len))
      );
    }
  }

  public BigEndianStream AsWriteableStream()
  {
    unsafe
    {
      return new BigEndianStream(
          new UnmanagedMemoryStream(
              (byte*)data.ToPointer(),
              Convert.ToInt64(capacity),
              Convert.ToInt64(capacity),
              FileAccess.Write
          )
      );
    }
  }
}

// This is a helper for safely passing byte references into the rust code.
// It's not actually used at the moment, because there aren't many things that you
// can take a direct pointer to managed memory, and if we're going to copy something
// then we might as well copy it into a `RustBuffer`. But it's here for API
// completeness.

[StructLayout(LayoutKind.Sequential)]
internal struct ForeignBytes
{
  public int length;
  public IntPtr data;
}


// The FfiConverter interface handles converter types to and from the FFI
//
// All implementing objects should be public to support external types.  When a
// type is external we need to import it's FfiConverter.
internal abstract class FfiConverter<CsType, FfiType>
{
  // Convert an FFI type to a C# type
  public abstract CsType Lift(FfiType value);

  // Convert C# type to an FFI type
  public abstract FfiType Lower(CsType value);

  // Read a C# type from a `ByteBuffer`
  public abstract CsType Read(BigEndianStream stream);

  // Calculate bytes to allocate when creating a `RustBuffer`
  //
  // This must return at least as many bytes as the write() function will
  // write. It can return more bytes than needed, for example when writing
  // Strings we can't know the exact bytes needed until we the UTF-8
  // encoding, so we pessimistically allocate the largest size possible (3
  // bytes per codepoint).  Allocating extra bytes is not really a big deal
  // because the `RustBuffer` is short-lived.
  public abstract int AllocationSize(CsType value);

  // Write a C# type to a `ByteBuffer`
  public abstract void Write(CsType value, BigEndianStream stream);

  // Lower a value into a `RustBuffer`
  //
  // This method lowers a value into a `RustBuffer` rather than the normal
  // FfiType.  It's used by the callback interface code.  Callback interface
  // returns are always serialized into a `RustBuffer` regardless of their
  // normal FFI type.
  public RustBuffer LowerIntoRustBuffer(CsType value)
  {
    var rbuf = RustBuffer.Alloc(AllocationSize(value));
    try
    {
      var stream = rbuf.AsWriteableStream();
      Write(value, stream);
      rbuf.len = Convert.ToUInt64(stream.Position);
      return rbuf;
    }
    catch
    {
      RustBuffer.Free(rbuf);
      throw;
    }
  }

  // Lift a value from a `RustBuffer`.
  //
  // This here mostly because of the symmetry with `lowerIntoRustBuffer()`.
  // It's currently only used by the `FfiConverterRustBuffer` class below.
  protected CsType LiftFromRustBuffer(RustBuffer rbuf)
  {
    var stream = rbuf.AsStream();
    try
    {
      var item = Read(stream);
      if (stream.HasRemaining())
      {
        throw new InternalException("junk remaining in buffer after lifting, something is very wrong!!");
      }
      return item;
    }
    finally
    {
      RustBuffer.Free(rbuf);
    }
  }
}

// FfiConverter that uses `RustBuffer` as the FfiType
internal abstract class FfiConverterRustBuffer<CsType> : FfiConverter<CsType, RustBuffer>
{
  public override CsType Lift(RustBuffer value)
  {
    return LiftFromRustBuffer(value);
  }
  public override RustBuffer Lower(CsType value)
  {
    return LowerIntoRustBuffer(value);
  }
}


// A handful of classes and functions to support the generated data structures.
// This would be a good candidate for isolating in its own ffi-support lib.
// Error runtime.
[StructLayout(LayoutKind.Sequential)]
struct UniffiRustCallStatus
{
  public sbyte code;
  public RustBuffer error_buf;

  public bool IsSuccess()
  {
    return code == 0;
  }

  public bool IsError()
  {
    return code == 1;
  }

  public bool IsPanic()
  {
    return code == 2;
  }
}

// Base class for all uniffi exceptions
public class UniffiException : System.Exception
{
  public UniffiException() : base() { }
  public UniffiException(string message) : base(message) { }
}

public class UndeclaredErrorException : UniffiException
{
  public UndeclaredErrorException(string message) : base(message) { }
}

public class PanicException : UniffiException
{
  public PanicException(string message) : base(message) { }
}

public class AllocationException : UniffiException
{
  public AllocationException(string message) : base(message) { }
}

public class InternalException : UniffiException
{
  public InternalException(string message) : base(message) { }
}

public class InvalidEnumException : InternalException
{
  public InvalidEnumException(string message) : base(message)
  {
  }
}

public class UniffiContractVersionException : UniffiException
{
  public UniffiContractVersionException(string message) : base(message)
  {
  }
}

public class UniffiContractChecksumException : UniffiException
{
  public UniffiContractChecksumException(string message) : base(message)
  {
  }
}

// Each top-level error class has a companion object that can lift the error from the call status's rust buffer
interface CallStatusErrorHandler<E> where E : System.Exception
{
  E Lift(RustBuffer error_buf);
}

// CallStatusErrorHandler implementation for times when we don't expect a CALL_ERROR
class NullCallStatusErrorHandler : CallStatusErrorHandler<UniffiException>
{
  public static NullCallStatusErrorHandler INSTANCE = new NullCallStatusErrorHandler();

  public UniffiException Lift(RustBuffer error_buf)
  {
    RustBuffer.Free(error_buf);
    return new UndeclaredErrorException("library has returned an error not declared in UNIFFI interface file");
  }
}

// Helpers for calling Rust
// In practice we usually need to be synchronized to call this safely, so it doesn't
// synchronize itself
class _UniffiHelpers
{
  public delegate void RustCallAction(ref UniffiRustCallStatus status);
  public delegate U RustCallFunc<out U>(ref UniffiRustCallStatus status);

  // Call a rust function that returns a Result<>.  Pass in the Error class companion that corresponds to the Err
  public static U RustCallWithError<U, E>(CallStatusErrorHandler<E> errorHandler, RustCallFunc<U> callback)
      where E : UniffiException
  {
    var status = new UniffiRustCallStatus();
    var return_value = callback(ref status);
    if (status.IsSuccess())
    {
      return return_value;
    }
    else if (status.IsError())
    {
      throw errorHandler.Lift(status.error_buf);
    }
    else if (status.IsPanic())
    {
      // when the rust code sees a panic, it tries to construct a rustbuffer
      // with the message.  but if that code panics, then it just sends back
      // an empty buffer.
      if (status.error_buf.len > 0)
      {
        throw new PanicException(FfiConverterString.INSTANCE.Lift(status.error_buf));
      }
      else
      {
        throw new PanicException("Rust panic");
      }
    }
    else
    {
      throw new InternalException($"Unknown rust call status: {status.code}");
    }
  }

  // Call a rust function that returns a Result<>.  Pass in the Error class companion that corresponds to the Err
  public static void RustCallWithError<E>(CallStatusErrorHandler<E> errorHandler, RustCallAction callback)
      where E : UniffiException
  {
    _UniffiHelpers.RustCallWithError(errorHandler, (ref UniffiRustCallStatus status) => {
      callback(ref status);
      return 0;
    });
  }

  // Call a rust function that returns a plain value
  public static U RustCall<U>(RustCallFunc<U> callback)
  {
    return _UniffiHelpers.RustCallWithError(NullCallStatusErrorHandler.INSTANCE, callback);
  }

  // Call a rust function that returns a plain value
  public static void RustCall(RustCallAction callback)
  {
    _UniffiHelpers.RustCall((ref UniffiRustCallStatus status) => {
      callback(ref status);
      return 0;
    });
  }
}

static class FFIObjectUtil
{
  public static void DisposeAll(params Object?[] list)
  {
    foreach (var obj in list)
    {
      Dispose(obj);
    }
  }

  // Dispose is implemented by recursive type inspection at runtime. This is because
  // generating correct Dispose calls for recursive complex types, e.g. List<List<int>>
  // is quite cumbersome.
  private static void Dispose(dynamic? obj)
  {
    if (obj == null)
    {
      return;
    }

    if (obj is IDisposable disposable)
    {
      disposable.Dispose();
      return;
    }

    var type = obj.GetType();
    if (type != null)
    {
      if (type.IsGenericType)
      {
        if (type.GetGenericTypeDefinition().IsAssignableFrom(typeof(List<>)))
        {
          foreach (var value in obj)
          {
            Dispose(value);
          }
        }
        else if (type.GetGenericTypeDefinition().IsAssignableFrom(typeof(Dictionary<,>)))
        {
          foreach (var value in obj.Values)
          {
            Dispose(value);
          }
        }
      }
    }
  }
}


// Big endian streams are not yet available in dotnet :'(
// https://github.com/dotnet/runtime/issues/26904

class StreamUnderflowException : System.Exception
{
  public StreamUnderflowException()
  {
  }
}

class BigEndianStream
{
  Stream stream;
  public BigEndianStream(Stream stream)
  {
    this.stream = stream;
  }

  public bool HasRemaining()
  {
    return (stream.Length - stream.Position) > 0;
  }

  public long Position
  {
    get => stream.Position;
    set => stream.Position = value;
  }

  public void WriteBytes(byte[] value)
  {
    stream.Write(value, 0, value.Length);
  }

  public void WriteByte(byte value)
  {
    stream.WriteByte(value);
  }

  public void WriteUShort(ushort value)
  {
    stream.WriteByte((byte)(value >> 8));
    stream.WriteByte((byte)value);
  }

  public void WriteUInt(uint value)
  {
    stream.WriteByte((byte)(value >> 24));
    stream.WriteByte((byte)(value >> 16));
    stream.WriteByte((byte)(value >> 8));
    stream.WriteByte((byte)value);
  }

  public void WriteULong(ulong value)
  {
    WriteUInt((uint)(value >> 32));
    WriteUInt((uint)value);
  }

  public void WriteSByte(sbyte value)
  {
    stream.WriteByte((byte)value);
  }

  public void WriteShort(short value)
  {
    WriteUShort((ushort)value);
  }

  public void WriteInt(int value)
  {
    WriteUInt((uint)value);
  }

  public void WriteFloat(float value)
  {
    unsafe
    {
      WriteInt(*((int*)&value));
    }
  }

  public void WriteLong(long value)
  {
    WriteULong((ulong)value);
  }

  public void WriteDouble(double value)
  {
    WriteLong(BitConverter.DoubleToInt64Bits(value));
  }

  public byte[] ReadBytes(int length)
  {
    CheckRemaining(length);
    byte[] result = new byte[length];
    stream.Read(result, 0, length);
    return result;
  }

  public byte ReadByte()
  {
    CheckRemaining(1);
    return Convert.ToByte(stream.ReadByte());
  }

  public ushort ReadUShort()
  {
    CheckRemaining(2);
    return (ushort)(stream.ReadByte() << 8 | stream.ReadByte());
  }

  public uint ReadUInt()
  {
    CheckRemaining(4);
    return (uint)(stream.ReadByte() << 24
        | stream.ReadByte() << 16
        | stream.ReadByte() << 8
        | stream.ReadByte());
  }

  public ulong ReadULong()
  {
    return (ulong)ReadUInt() << 32 | (ulong)ReadUInt();
  }

  public sbyte ReadSByte()
  {
    return (sbyte)ReadByte();
  }

  public short ReadShort()
  {
    return (short)ReadUShort();
  }

  public int ReadInt()
  {
    return (int)ReadUInt();
  }

  public float ReadFloat()
  {
    unsafe
    {
      int value = ReadInt();
      return *((float*)&value);
    }
  }

  public long ReadLong()
  {
    return (long)ReadULong();
  }

  public double ReadDouble()
  {
    return BitConverter.Int64BitsToDouble(ReadLong());
  }

  private void CheckRemaining(int length)
  {
    if (stream.Length - stream.Position < length)
    {
      throw new StreamUnderflowException();
    }
  }
}

// Contains loading, initialization code,
// and the FFI Function declarations in a com.sun.jna.Library.


// This is an implementation detail that will be called internally by the public API.
static partial class _UniFFILib
{
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  public delegate void UniffiRustFutureContinuationCallback(
      ulong @data, sbyte @pollResult
  );
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  public delegate void UniffiForeignFutureFree(
      ulong @handle
  );
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  public delegate void UniffiCallbackInterfaceFree(
      ulong @handle
  );
  [StructLayout(LayoutKind.Sequential)]
  public struct UniffiForeignFuture
  {
    public ulong @handle;
    public UniffiForeignFutureFree @free;
  }
  [StructLayout(LayoutKind.Sequential)]
  public struct UniffiForeignFutureStructU8
  {
    public byte @returnValue;
    public UniffiRustCallStatus @callStatus;
  }
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  public delegate void UniffiForeignFutureCompleteU8(
      ulong @callbackData, UniffiForeignFutureStructU8 @result
  );
  [StructLayout(LayoutKind.Sequential)]
  public struct UniffiForeignFutureStructI8
  {
    public sbyte @returnValue;
    public UniffiRustCallStatus @callStatus;
  }
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  public delegate void UniffiForeignFutureCompleteI8(
      ulong @callbackData, UniffiForeignFutureStructI8 @result
  );
  [StructLayout(LayoutKind.Sequential)]
  public struct UniffiForeignFutureStructU16
  {
    public ushort @returnValue;
    public UniffiRustCallStatus @callStatus;
  }
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  public delegate void UniffiForeignFutureCompleteU16(
      ulong @callbackData, UniffiForeignFutureStructU16 @result
  );
  [StructLayout(LayoutKind.Sequential)]
  public struct UniffiForeignFutureStructI16
  {
    public short @returnValue;
    public UniffiRustCallStatus @callStatus;
  }
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  public delegate void UniffiForeignFutureCompleteI16(
      ulong @callbackData, UniffiForeignFutureStructI16 @result
  );
  [StructLayout(LayoutKind.Sequential)]
  public struct UniffiForeignFutureStructU32
  {
    public uint @returnValue;
    public UniffiRustCallStatus @callStatus;
  }
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  public delegate void UniffiForeignFutureCompleteU32(
      ulong @callbackData, UniffiForeignFutureStructU32 @result
  );
  [StructLayout(LayoutKind.Sequential)]
  public struct UniffiForeignFutureStructI32
  {
    public int @returnValue;
    public UniffiRustCallStatus @callStatus;
  }
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  public delegate void UniffiForeignFutureCompleteI32(
      ulong @callbackData, UniffiForeignFutureStructI32 @result
  );
  [StructLayout(LayoutKind.Sequential)]
  public struct UniffiForeignFutureStructU64
  {
    public ulong @returnValue;
    public UniffiRustCallStatus @callStatus;
  }
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  public delegate void UniffiForeignFutureCompleteU64(
      ulong @callbackData, UniffiForeignFutureStructU64 @result
  );
  [StructLayout(LayoutKind.Sequential)]
  public struct UniffiForeignFutureStructI64
  {
    public long @returnValue;
    public UniffiRustCallStatus @callStatus;
  }
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  public delegate void UniffiForeignFutureCompleteI64(
      ulong @callbackData, UniffiForeignFutureStructI64 @result
  );
  [StructLayout(LayoutKind.Sequential)]
  public struct UniffiForeignFutureStructF32
  {
    public float @returnValue;
    public UniffiRustCallStatus @callStatus;
  }
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  public delegate void UniffiForeignFutureCompleteF32(
      ulong @callbackData, UniffiForeignFutureStructF32 @result
  );
  [StructLayout(LayoutKind.Sequential)]
  public struct UniffiForeignFutureStructF64
  {
    public double @returnValue;
    public UniffiRustCallStatus @callStatus;
  }
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  public delegate void UniffiForeignFutureCompleteF64(
      ulong @callbackData, UniffiForeignFutureStructF64 @result
  );
  [StructLayout(LayoutKind.Sequential)]
  public struct UniffiForeignFutureStructPointer
  {
    public IntPtr @returnValue;
    public UniffiRustCallStatus @callStatus;
  }
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  public delegate void UniffiForeignFutureCompletePointer(
      ulong @callbackData, UniffiForeignFutureStructPointer @result
  );
  [StructLayout(LayoutKind.Sequential)]
  public struct UniffiForeignFutureStructRustBuffer
  {
    public RustBuffer @returnValue;
    public UniffiRustCallStatus @callStatus;
  }
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  public delegate void UniffiForeignFutureCompleteRustBuffer(
      ulong @callbackData, UniffiForeignFutureStructRustBuffer @result
  );
  [StructLayout(LayoutKind.Sequential)]
  public struct UniffiForeignFutureStructVoid
  {
    public UniffiRustCallStatus @callStatus;
  }
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  public delegate void UniffiForeignFutureCompleteVoid(
      ulong @callbackData, UniffiForeignFutureStructVoid @result
  );












































































































































  static _UniFFILib()
  {
    _UniFFILib.uniffiCheckContractApiVersion();
    _UniFFILib.uniffiCheckApiChecksums();

  }

  [LibraryImport("iotzio_core")]
  public static partial IntPtr uniffi_iotzio_fn_clone_i2cbus(
  IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void uniffi_iotzio_fn_free_i2cbus(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial RustBuffer uniffi_iotzio_fn_method_i2cbus_read(IntPtr @ptr, ushort @address, RustBuffer @buffer, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void uniffi_iotzio_fn_method_i2cbus_write(IntPtr @ptr, ushort @address, RustBuffer @bytes, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial RustBuffer uniffi_iotzio_fn_method_i2cbus_write_read(IntPtr @ptr, ushort @address, RustBuffer @bytes, RustBuffer @buffer, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial IntPtr uniffi_iotzio_fn_clone_inputpin(
  IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void uniffi_iotzio_fn_free_inputpin(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial RustBuffer uniffi_iotzio_fn_method_inputpin_get_level(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial RustBuffer uniffi_iotzio_fn_method_inputpin_get_pin(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial RustBuffer uniffi_iotzio_fn_method_inputpin_get_pull_setting(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial sbyte uniffi_iotzio_fn_method_inputpin_is_high(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial sbyte uniffi_iotzio_fn_method_inputpin_is_hysteresis_enabled(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial sbyte uniffi_iotzio_fn_method_inputpin_is_low(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void uniffi_iotzio_fn_method_inputpin_wait_for_any_edge(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial RustBuffer uniffi_iotzio_fn_method_inputpin_wait_for_any_pulse(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void uniffi_iotzio_fn_method_inputpin_wait_for_falling_edge(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void uniffi_iotzio_fn_method_inputpin_wait_for_high(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial RustBuffer uniffi_iotzio_fn_method_inputpin_wait_for_high_pulse(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void uniffi_iotzio_fn_method_inputpin_wait_for_low(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial RustBuffer uniffi_iotzio_fn_method_inputpin_wait_for_low_pulse(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void uniffi_iotzio_fn_method_inputpin_wait_for_rising_edge(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial IntPtr uniffi_iotzio_fn_clone_iotzio(
  IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void uniffi_iotzio_fn_free_iotzio(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_fn_method_iotzio_protocol_version(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial ulong uniffi_iotzio_fn_method_iotzio_runtime_identifier(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial RustBuffer uniffi_iotzio_fn_method_iotzio_serial_number(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial IntPtr uniffi_iotzio_fn_method_iotzio_setup_i2c_bus(IntPtr @ptr, RustBuffer @config, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial IntPtr uniffi_iotzio_fn_method_iotzio_setup_input_pin(IntPtr @ptr, RustBuffer @pin, RustBuffer @pullSetting, sbyte @hysteresis, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial IntPtr uniffi_iotzio_fn_method_iotzio_setup_output_pin(IntPtr @ptr, RustBuffer @pin, RustBuffer @initialLevel, RustBuffer @driveStrength, RustBuffer @slewRate, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial RustBuffer uniffi_iotzio_fn_method_iotzio_version(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial IntPtr uniffi_iotzio_fn_clone_iotzioinfo(
  IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void uniffi_iotzio_fn_free_iotzioinfo(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial IntPtr uniffi_iotzio_fn_method_iotzioinfo_open(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial ulong uniffi_iotzio_fn_method_iotzioinfo_runtime_identifier(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial RustBuffer uniffi_iotzio_fn_method_iotzioinfo_serial_number(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial RustBuffer uniffi_iotzio_fn_method_iotzioinfo_version(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial IntPtr uniffi_iotzio_fn_clone_iotziomanager(
  IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void uniffi_iotzio_fn_free_iotziomanager(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial IntPtr uniffi_iotzio_fn_constructor_iotziomanager_new(ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial RustBuffer uniffi_iotzio_fn_method_iotziomanager_list_connected_boards(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial IntPtr uniffi_iotzio_fn_clone_outputpin(
  IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void uniffi_iotzio_fn_free_outputpin(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial RustBuffer uniffi_iotzio_fn_method_outputpin_get_drive_strength(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial RustBuffer uniffi_iotzio_fn_method_outputpin_get_level(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial RustBuffer uniffi_iotzio_fn_method_outputpin_get_pin(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial RustBuffer uniffi_iotzio_fn_method_outputpin_get_slew_rate(IntPtr @ptr, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void uniffi_iotzio_fn_method_outputpin_set_level(IntPtr @ptr, RustBuffer @level, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial RustBuffer ffi_iotzio_rustbuffer_alloc(ulong @size, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial RustBuffer ffi_iotzio_rustbuffer_from_bytes(ForeignBytes @bytes, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rustbuffer_free(RustBuffer @buf, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial RustBuffer ffi_iotzio_rustbuffer_reserve(RustBuffer @buf, ulong @additional, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_poll_u8(long @handle, UniffiRustFutureContinuationCallback @callback, long @callbackData
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_cancel_u8(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_free_u8(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial byte ffi_iotzio_rust_future_complete_u8(long @handle, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_poll_i8(long @handle, UniffiRustFutureContinuationCallback @callback, long @callbackData
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_cancel_i8(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_free_i8(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial sbyte ffi_iotzio_rust_future_complete_i8(long @handle, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_poll_u16(long @handle, UniffiRustFutureContinuationCallback @callback, long @callbackData
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_cancel_u16(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_free_u16(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort ffi_iotzio_rust_future_complete_u16(long @handle, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_poll_i16(long @handle, UniffiRustFutureContinuationCallback @callback, long @callbackData
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_cancel_i16(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_free_i16(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial short ffi_iotzio_rust_future_complete_i16(long @handle, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_poll_u32(long @handle, UniffiRustFutureContinuationCallback @callback, long @callbackData
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_cancel_u32(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_free_u32(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial uint ffi_iotzio_rust_future_complete_u32(long @handle, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_poll_i32(long @handle, UniffiRustFutureContinuationCallback @callback, long @callbackData
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_cancel_i32(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_free_i32(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial int ffi_iotzio_rust_future_complete_i32(long @handle, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_poll_u64(long @handle, UniffiRustFutureContinuationCallback @callback, long @callbackData
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_cancel_u64(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_free_u64(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial ulong ffi_iotzio_rust_future_complete_u64(long @handle, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_poll_i64(long @handle, UniffiRustFutureContinuationCallback @callback, long @callbackData
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_cancel_i64(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_free_i64(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial long ffi_iotzio_rust_future_complete_i64(long @handle, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_poll_f32(long @handle, UniffiRustFutureContinuationCallback @callback, long @callbackData
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_cancel_f32(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_free_f32(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial float ffi_iotzio_rust_future_complete_f32(long @handle, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_poll_f64(long @handle, UniffiRustFutureContinuationCallback @callback, long @callbackData
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_cancel_f64(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_free_f64(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial double ffi_iotzio_rust_future_complete_f64(long @handle, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_poll_pointer(long @handle, UniffiRustFutureContinuationCallback @callback, long @callbackData
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_cancel_pointer(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_free_pointer(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial IntPtr ffi_iotzio_rust_future_complete_pointer(long @handle, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_poll_rust_buffer(long @handle, UniffiRustFutureContinuationCallback @callback, long @callbackData
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_cancel_rust_buffer(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_free_rust_buffer(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial RustBuffer ffi_iotzio_rust_future_complete_rust_buffer(long @handle, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_poll_void(long @handle, UniffiRustFutureContinuationCallback @callback, long @callbackData
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_cancel_void(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_free_void(long @handle
  );

  [LibraryImport("iotzio_core")]
  public static partial void ffi_iotzio_rust_future_complete_void(long @handle, ref UniffiRustCallStatus _uniffi_out_err
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_i2cbus_read(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_i2cbus_write(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_i2cbus_write_read(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_inputpin_get_level(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_inputpin_get_pin(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_inputpin_get_pull_setting(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_inputpin_is_high(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_inputpin_is_hysteresis_enabled(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_inputpin_is_low(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_inputpin_wait_for_any_edge(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_inputpin_wait_for_any_pulse(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_inputpin_wait_for_falling_edge(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_inputpin_wait_for_high(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_inputpin_wait_for_high_pulse(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_inputpin_wait_for_low(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_inputpin_wait_for_low_pulse(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_inputpin_wait_for_rising_edge(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_iotzio_protocol_version(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_iotzio_runtime_identifier(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_iotzio_serial_number(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_iotzio_setup_i2c_bus(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_iotzio_setup_input_pin(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_iotzio_setup_output_pin(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_iotzio_version(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_iotzioinfo_open(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_iotzioinfo_runtime_identifier(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_iotzioinfo_serial_number(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_iotzioinfo_version(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_iotziomanager_list_connected_boards(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_outputpin_get_drive_strength(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_outputpin_get_level(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_outputpin_get_pin(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_outputpin_get_slew_rate(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_method_outputpin_set_level(
  );

  [LibraryImport("iotzio_core")]
  public static partial ushort uniffi_iotzio_checksum_constructor_iotziomanager_new(
  );

  [LibraryImport("iotzio_core")]
  public static partial uint ffi_iotzio_uniffi_contract_version(
  );



  static void uniffiCheckContractApiVersion()
  {
    var scaffolding_contract_version = _UniFFILib.ffi_iotzio_uniffi_contract_version();
    if (26 != scaffolding_contract_version)
    {
      throw new UniffiContractVersionException($"Com.Iotzio.Api: uniffi bindings expected version `26`, library returned `{scaffolding_contract_version}`");
    }
  }

  static void uniffiCheckApiChecksums()
  {
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_i2cbus_read();
      if (checksum != 64098)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_i2cbus_read` checksum `64098`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_i2cbus_write();
      if (checksum != 5465)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_i2cbus_write` checksum `5465`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_i2cbus_write_read();
      if (checksum != 11683)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_i2cbus_write_read` checksum `11683`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_inputpin_get_level();
      if (checksum != 458)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_inputpin_get_level` checksum `458`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_inputpin_get_pin();
      if (checksum != 18595)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_inputpin_get_pin` checksum `18595`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_inputpin_get_pull_setting();
      if (checksum != 54251)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_inputpin_get_pull_setting` checksum `54251`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_inputpin_is_high();
      if (checksum != 56157)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_inputpin_is_high` checksum `56157`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_inputpin_is_hysteresis_enabled();
      if (checksum != 53361)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_inputpin_is_hysteresis_enabled` checksum `53361`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_inputpin_is_low();
      if (checksum != 57363)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_inputpin_is_low` checksum `57363`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_inputpin_wait_for_any_edge();
      if (checksum != 26549)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_inputpin_wait_for_any_edge` checksum `26549`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_inputpin_wait_for_any_pulse();
      if (checksum != 57682)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_inputpin_wait_for_any_pulse` checksum `57682`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_inputpin_wait_for_falling_edge();
      if (checksum != 27788)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_inputpin_wait_for_falling_edge` checksum `27788`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_inputpin_wait_for_high();
      if (checksum != 48802)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_inputpin_wait_for_high` checksum `48802`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_inputpin_wait_for_high_pulse();
      if (checksum != 3753)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_inputpin_wait_for_high_pulse` checksum `3753`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_inputpin_wait_for_low();
      if (checksum != 51254)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_inputpin_wait_for_low` checksum `51254`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_inputpin_wait_for_low_pulse();
      if (checksum != 31673)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_inputpin_wait_for_low_pulse` checksum `31673`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_inputpin_wait_for_rising_edge();
      if (checksum != 36012)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_inputpin_wait_for_rising_edge` checksum `36012`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_iotzio_protocol_version();
      if (checksum != 32805)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_iotzio_protocol_version` checksum `32805`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_iotzio_runtime_identifier();
      if (checksum != 43114)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_iotzio_runtime_identifier` checksum `43114`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_iotzio_serial_number();
      if (checksum != 54935)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_iotzio_serial_number` checksum `54935`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_iotzio_setup_i2c_bus();
      if (checksum != 4850)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_iotzio_setup_i2c_bus` checksum `4850`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_iotzio_setup_input_pin();
      if (checksum != 24594)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_iotzio_setup_input_pin` checksum `24594`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_iotzio_setup_output_pin();
      if (checksum != 45480)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_iotzio_setup_output_pin` checksum `45480`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_iotzio_version();
      if (checksum != 49378)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_iotzio_version` checksum `49378`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_iotzioinfo_open();
      if (checksum != 34598)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_iotzioinfo_open` checksum `34598`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_iotzioinfo_runtime_identifier();
      if (checksum != 56218)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_iotzioinfo_runtime_identifier` checksum `56218`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_iotzioinfo_serial_number();
      if (checksum != 6253)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_iotzioinfo_serial_number` checksum `6253`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_iotzioinfo_version();
      if (checksum != 40145)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_iotzioinfo_version` checksum `40145`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_iotziomanager_list_connected_boards();
      if (checksum != 8626)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_iotziomanager_list_connected_boards` checksum `8626`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_outputpin_get_drive_strength();
      if (checksum != 21285)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_outputpin_get_drive_strength` checksum `21285`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_outputpin_get_level();
      if (checksum != 53034)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_outputpin_get_level` checksum `53034`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_outputpin_get_pin();
      if (checksum != 6179)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_outputpin_get_pin` checksum `6179`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_outputpin_get_slew_rate();
      if (checksum != 10986)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_outputpin_get_slew_rate` checksum `10986`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_method_outputpin_set_level();
      if (checksum != 2401)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_method_outputpin_set_level` checksum `2401`, library returned `{checksum}`");
      }
    }
    {
      var checksum = _UniFFILib.uniffi_iotzio_checksum_constructor_iotziomanager_new();
      if (checksum != 57914)
      {
        throw new UniffiContractChecksumException($"Com.Iotzio.Api: uniffi bindings expected function `uniffi_iotzio_checksum_constructor_iotziomanager_new` checksum `57914`, library returned `{checksum}`");
      }
    }
  }
}

// Public interface members begin here.

#pragma warning disable 8625




class FfiConverterUInt16 : FfiConverter<ushort, ushort>
{
  public static FfiConverterUInt16 INSTANCE = new FfiConverterUInt16();

  public override ushort Lift(ushort value)
  {
    return value;
  }

  public override ushort Read(BigEndianStream stream)
  {
    return stream.ReadUShort();
  }

  public override ushort Lower(ushort value)
  {
    return value;
  }

  public override int AllocationSize(ushort value)
  {
    return 2;
  }

  public override void Write(ushort value, BigEndianStream stream)
  {
    stream.WriteUShort(value);
  }
}



class FfiConverterUInt32 : FfiConverter<uint, uint>
{
  public static FfiConverterUInt32 INSTANCE = new FfiConverterUInt32();

  public override uint Lift(uint value)
  {
    return value;
  }

  public override uint Read(BigEndianStream stream)
  {
    return stream.ReadUInt();
  }

  public override uint Lower(uint value)
  {
    return value;
  }

  public override int AllocationSize(uint value)
  {
    return 4;
  }

  public override void Write(uint value, BigEndianStream stream)
  {
    stream.WriteUInt(value);
  }
}



class FfiConverterUInt64 : FfiConverter<ulong, ulong>
{
  public static FfiConverterUInt64 INSTANCE = new FfiConverterUInt64();

  public override ulong Lift(ulong value)
  {
    return value;
  }

  public override ulong Read(BigEndianStream stream)
  {
    return stream.ReadULong();
  }

  public override ulong Lower(ulong value)
  {
    return value;
  }

  public override int AllocationSize(ulong value)
  {
    return 8;
  }

  public override void Write(ulong value, BigEndianStream stream)
  {
    stream.WriteULong(value);
  }
}



class FfiConverterBoolean : FfiConverter<bool, sbyte>
{
  public static FfiConverterBoolean INSTANCE = new FfiConverterBoolean();

  public override bool Lift(sbyte value)
  {
    return value != 0;
  }

  public override bool Read(BigEndianStream stream)
  {
    return Lift(stream.ReadSByte());
  }

  public override sbyte Lower(bool value)
  {
    return value ? (sbyte)1 : (sbyte)0;
  }

  public override int AllocationSize(bool value)
  {
    return (sbyte)1;
  }

  public override void Write(bool value, BigEndianStream stream)
  {
    stream.WriteSByte(Lower(value));
  }
}



class FfiConverterString : FfiConverter<string, RustBuffer>
{
  public static FfiConverterString INSTANCE = new FfiConverterString();

  // Note: we don't inherit from FfiConverterRustBuffer, because we use a
  // special encoding when lowering/lifting.  We can use `RustBuffer.len` to
  // store our length and avoid writing it out to the buffer.
  public override string Lift(RustBuffer value)
  {
    try
    {
      var bytes = value.AsStream().ReadBytes(Convert.ToInt32(value.len));
      return System.Text.Encoding.UTF8.GetString(bytes);
    }
    finally
    {
      RustBuffer.Free(value);
    }
  }

  public override string Read(BigEndianStream stream)
  {
    var length = stream.ReadInt();
    var bytes = stream.ReadBytes(length);
    return System.Text.Encoding.UTF8.GetString(bytes);
  }

  public override RustBuffer Lower(string value)
  {
    var bytes = System.Text.Encoding.UTF8.GetBytes(value);
    var rbuf = RustBuffer.Alloc(bytes.Length);
    rbuf.AsWriteableStream().WriteBytes(bytes);
    return rbuf;
  }

  // TODO(CS)
  // We aren't sure exactly how many bytes our string will be once it's UTF-8
  // encoded.  Allocate 3 bytes per unicode codepoint which will always be
  // enough.
  public override int AllocationSize(string value)
  {
    const int sizeForLength = 4;
    var sizeForString = System.Text.Encoding.UTF8.GetByteCount(value);
    return sizeForLength + sizeForString;
  }

  public override void Write(string value, BigEndianStream stream)
  {
    var bytes = System.Text.Encoding.UTF8.GetBytes(value);
    stream.WriteInt(bytes.Length);
    stream.WriteBytes(bytes);
  }
}




class FfiConverterByteArray : FfiConverterRustBuffer<byte[]>
{
  public static FfiConverterByteArray INSTANCE = new FfiConverterByteArray();

  public override byte[] Read(BigEndianStream stream)
  {
    var length = stream.ReadInt();
    return stream.ReadBytes(length);
  }

  public override int AllocationSize(byte[] value)
  {
    return 4 + value.Length;
  }

  public override void Write(byte[] value, BigEndianStream stream)
  {
    stream.WriteInt(value.Length);
    stream.WriteBytes(value);
  }
}




class FfiConverterDuration : FfiConverterRustBuffer<TimeSpan>
{
  public static FfiConverterDuration INSTANCE = new FfiConverterDuration();

  // https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/TimeSpan.cs
  private const uint NanosecondsPerTick = 100;

  public override TimeSpan Read(BigEndianStream stream)
  {
    var seconds = stream.ReadULong();
    var nanoseconds = stream.ReadUInt();
    var ticks = seconds * TimeSpan.TicksPerSecond;
    ticks += nanoseconds / NanosecondsPerTick;
    return new TimeSpan(Convert.ToInt64(ticks));
  }

  public override int AllocationSize(TimeSpan value)
  {
    // 8 bytes for seconds, 4 bytes for nanoseconds
    return 12;
  }

  public override void Write(TimeSpan value, BigEndianStream stream)
  {
    stream.WriteULong(Convert.ToUInt64(value.Ticks / TimeSpan.TicksPerSecond));
    stream.WriteUInt(Convert.ToUInt32(value.Ticks % TimeSpan.TicksPerSecond * NanosecondsPerTick));
  }
}




public abstract class FFIObject : IDisposable
{
  protected IntPtr pointer;

  private int _wasDestroyed = 0;
  private long _callCounter = 1;

  protected FFIObject(IntPtr pointer)
  {
    this.pointer = pointer;
  }

  protected abstract void FreeRustArcPtr();
  protected abstract void CloneRustArcPtr();

  public void Destroy()
  {
    // Only allow a single call to this method.
    if (Interlocked.CompareExchange(ref _wasDestroyed, 1, 0) == 0)
    {
      // This decrement always matches the initial count of 1 given at creation time.
      if (Interlocked.Decrement(ref _callCounter) == 0)
      {
        FreeRustArcPtr();
      }
    }
  }

  public void Dispose()
  {
    Destroy();
    GC.SuppressFinalize(this); // Suppress finalization to avoid unnecessary GC overhead.
  }

  ~FFIObject()
  {
    Destroy();
  }

  private void IncrementCallCounter()
  {
    // Check and increment the call counter, to keep the object alive.
    // This needs a compare-and-set retry loop in case of concurrent updates.
    long count;
    do
    {
      count = Interlocked.Read(ref _callCounter);
      if (count == 0L) throw new System.ObjectDisposedException(String.Format("'{0}' object has already been destroyed", this.GetType().Name));
      if (count == long.MaxValue) throw new System.OverflowException(String.Format("'{0}' call counter would overflow", this.GetType().Name));

    } while (Interlocked.CompareExchange(ref _callCounter, count + 1, count) != count);
  }

  private void DecrementCallCounter()
  {
    // This decrement always matches the increment we performed above.
    if (Interlocked.Decrement(ref _callCounter) == 0)
    {
      FreeRustArcPtr();
    }
  }

  internal void CallWithPointer(Action<IntPtr> action)
  {
    IncrementCallCounter();
    try
    {
      CloneRustArcPtr();
      action(this.pointer);
    }
    finally
    {
      DecrementCallCounter();
    }
  }

  internal T CallWithPointer<T>(Func<IntPtr, T> func)
  {
    IncrementCallCounter();
    try
    {
      CloneRustArcPtr();
      return func(this.pointer);
    }
    finally
    {
      DecrementCallCounter();
    }
  }
}
public interface II2cBus
{
  /// <summary>
  /// Read from address into buffer. Returns buffer.
  /// </summary>
  /// <exception cref="I2cBusModuleException"></exception>
  byte[] Read(ushort @address, byte[] @buffer);
  /// <summary>
  /// Write to address from bytes.
  /// </summary>
  /// <exception cref="I2cBusModuleException"></exception>
  void Write(ushort @address, byte[] @bytes);
  /// <summary>
  /// Write to address from bytes, read from address into buffer. Returns buffer.
  /// </summary>
  /// <exception cref="I2cBusModuleException"></exception>
  byte[] WriteRead(ushort @address, byte[] @bytes, byte[] @buffer);
}
public class I2cBus : FFIObject, II2cBus
{
  public I2cBus(IntPtr pointer) : base(pointer) { }

  protected override void FreeRustArcPtr()
  {
    _UniffiHelpers.RustCall((ref UniffiRustCallStatus status) => {
      _UniFFILib.uniffi_iotzio_fn_free_i2cbus(this.pointer, ref status);
    });
  }

  protected override void CloneRustArcPtr()
  {
    _UniffiHelpers.RustCall((ref UniffiRustCallStatus status) => {
      _UniFFILib.uniffi_iotzio_fn_clone_i2cbus(this.pointer, ref status);
    });
  }


  /// <summary>
  /// Read from address into buffer. Returns buffer.
  /// </summary>
  /// <exception cref="I2cBusModuleException"></exception>
  public byte[] Read(ushort @address, byte[] @buffer)
  {
    return CallWithPointer(thisPtr => FfiConverterByteArray.INSTANCE.Lift(
_UniffiHelpers.RustCallWithError(FfiConverterTypeI2cBusModuleError.INSTANCE, (ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_i2cbus_read(thisPtr, FfiConverterUInt16.INSTANCE.Lower(@address), FfiConverterByteArray.INSTANCE.Lower(@buffer), ref _status)
)));
  }


  /// <summary>
  /// Write to address from bytes.
  /// </summary>
  /// <exception cref="I2cBusModuleException"></exception>
  public void Write(ushort @address, byte[] @bytes)
  {
    CallWithPointer(thisPtr =>
_UniffiHelpers.RustCallWithError(FfiConverterTypeI2cBusModuleError.INSTANCE, (ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_i2cbus_write(thisPtr, FfiConverterUInt16.INSTANCE.Lower(@address), FfiConverterByteArray.INSTANCE.Lower(@bytes), ref _status)
));
  }



  /// <summary>
  /// Write to address from bytes, read from address into buffer. Returns buffer.
  /// </summary>
  /// <exception cref="I2cBusModuleException"></exception>
  public byte[] WriteRead(ushort @address, byte[] @bytes, byte[] @buffer)
  {
    return CallWithPointer(thisPtr => FfiConverterByteArray.INSTANCE.Lift(
_UniffiHelpers.RustCallWithError(FfiConverterTypeI2cBusModuleError.INSTANCE, (ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_i2cbus_write_read(thisPtr, FfiConverterUInt16.INSTANCE.Lower(@address), FfiConverterByteArray.INSTANCE.Lower(@bytes), FfiConverterByteArray.INSTANCE.Lower(@buffer), ref _status)
)));
  }




}

class FfiConverterTypeI2cBus : FfiConverter<I2cBus, IntPtr>
{
  public static FfiConverterTypeI2cBus INSTANCE = new FfiConverterTypeI2cBus();

  public override IntPtr Lower(I2cBus value)
  {
    return value.CallWithPointer(thisPtr => thisPtr);
  }

  public override I2cBus Lift(IntPtr value)
  {
    return new I2cBus(value);
  }

  public override I2cBus Read(BigEndianStream stream)
  {
    return Lift(new IntPtr(stream.ReadLong()));
  }

  public override int AllocationSize(I2cBus value)
  {
    return 8;
  }

  public override void Write(I2cBus value, BigEndianStream stream)
  {
    stream.WriteLong(Lower(value).ToInt64());
  }
}



public interface IInputPin
{
  /// <summary>
  /// Returns current pin level.
  /// </summary>
  /// <exception cref="InputPinModuleException"></exception>
  Level GetLevel();
  /// <summary>
  /// Returns used pin.
  /// </summary>
  GpioPin GetPin();
  /// <summary>
  /// Returns current pull setting.
  /// </summary>
  Pull GetPullSetting();
  /// <summary>
  /// Get whether the pin input level is high.
  /// </summary>
  /// <exception cref="InputPinModuleException"></exception>
  bool IsHigh();
  /// <summary>
  /// Returns whether hysteresis is enabled.
  /// </summary>
  bool IsHysteresisEnabled();
  /// <summary>
  /// Get whether the pin input level is low.
  /// </summary>
  /// <exception cref="InputPinModuleException"></exception>
  bool IsLow();
  /// <summary>
  /// Wait for the pin to undergo any transition, i.e low to high OR high to low.
  /// </summary>
  /// <exception cref="InputPinModuleException"></exception>
  void WaitForAnyEdge();
  /// <summary>
  /// Wait for the pin to undergo a pulse transition, i.e. from low to high to low again OR from high to low to high again. Returns pulse width when succeeded.
  /// </summary>
  /// <exception cref="InputPinModuleException"></exception>
  TimeSpan WaitForAnyPulse();
  /// <summary>
  /// Wait for the pin to undergo a transition from high to low.
  /// </summary>
  /// <exception cref="InputPinModuleException"></exception>
  void WaitForFallingEdge();
  /// <summary>
  /// Wait until the pin is high. If it is already high, return immediately.
  /// </summary>
  /// <exception cref="InputPinModuleException"></exception>
  void WaitForHigh();
  /// <summary>
  /// Wait for the pin to undergo a pulse transition from low to high to low again. Returns pulse width when succeeded.
  /// </summary>
  /// <exception cref="InputPinModuleException"></exception>
  TimeSpan WaitForHighPulse();
  /// <summary>
  /// Wait until the pin is low. If it is already low, return immediately.
  /// </summary>
  /// <exception cref="InputPinModuleException"></exception>
  void WaitForLow();
  /// <summary>
  /// Wait for the pin to undergo a pulse transition from high to low to high again. Returns pulse width when succeeded.
  /// </summary>
  /// <exception cref="InputPinModuleException"></exception>
  TimeSpan WaitForLowPulse();
  /// <summary>
  /// Wait for the pin to undergo a transition from low to high.
  /// </summary>
  /// <exception cref="InputPinModuleException"></exception>
  void WaitForRisingEdge();
}
public class InputPin : FFIObject, IInputPin
{
  public InputPin(IntPtr pointer) : base(pointer) { }

  protected override void FreeRustArcPtr()
  {
    _UniffiHelpers.RustCall((ref UniffiRustCallStatus status) => {
      _UniFFILib.uniffi_iotzio_fn_free_inputpin(this.pointer, ref status);
    });
  }

  protected override void CloneRustArcPtr()
  {
    _UniffiHelpers.RustCall((ref UniffiRustCallStatus status) => {
      _UniFFILib.uniffi_iotzio_fn_clone_inputpin(this.pointer, ref status);
    });
  }


  /// <summary>
  /// Returns current pin level.
  /// </summary>
  /// <exception cref="InputPinModuleException"></exception>
  public Level GetLevel()
  {
    return CallWithPointer(thisPtr => FfiConverterTypeLevel.INSTANCE.Lift(
_UniffiHelpers.RustCallWithError(FfiConverterTypeInputPinModuleError.INSTANCE, (ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_inputpin_get_level(thisPtr, ref _status)
)));
  }


  /// <summary>
  /// Returns used pin.
  /// </summary>
  public GpioPin GetPin()
  {
    return CallWithPointer(thisPtr => FfiConverterTypeGpioPin.INSTANCE.Lift(
_UniffiHelpers.RustCall((ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_inputpin_get_pin(thisPtr, ref _status)
)));
  }


  /// <summary>
  /// Returns current pull setting.
  /// </summary>
  public Pull GetPullSetting()
  {
    return CallWithPointer(thisPtr => FfiConverterTypePull.INSTANCE.Lift(
_UniffiHelpers.RustCall((ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_inputpin_get_pull_setting(thisPtr, ref _status)
)));
  }


  /// <summary>
  /// Get whether the pin input level is high.
  /// </summary>
  /// <exception cref="InputPinModuleException"></exception>
  public bool IsHigh()
  {
    return CallWithPointer(thisPtr => FfiConverterBoolean.INSTANCE.Lift(
_UniffiHelpers.RustCallWithError(FfiConverterTypeInputPinModuleError.INSTANCE, (ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_inputpin_is_high(thisPtr, ref _status)
)));
  }


  /// <summary>
  /// Returns whether hysteresis is enabled.
  /// </summary>
  public bool IsHysteresisEnabled()
  {
    return CallWithPointer(thisPtr => FfiConverterBoolean.INSTANCE.Lift(
_UniffiHelpers.RustCall((ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_inputpin_is_hysteresis_enabled(thisPtr, ref _status)
)));
  }


  /// <summary>
  /// Get whether the pin input level is low.
  /// </summary>
  /// <exception cref="InputPinModuleException"></exception>
  public bool IsLow()
  {
    return CallWithPointer(thisPtr => FfiConverterBoolean.INSTANCE.Lift(
_UniffiHelpers.RustCallWithError(FfiConverterTypeInputPinModuleError.INSTANCE, (ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_inputpin_is_low(thisPtr, ref _status)
)));
  }


  /// <summary>
  /// Wait for the pin to undergo any transition, i.e low to high OR high to low.
  /// </summary>
  /// <exception cref="InputPinModuleException"></exception>
  public void WaitForAnyEdge()
  {
    CallWithPointer(thisPtr =>
_UniffiHelpers.RustCallWithError(FfiConverterTypeInputPinModuleError.INSTANCE, (ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_inputpin_wait_for_any_edge(thisPtr, ref _status)
));
  }



  /// <summary>
  /// Wait for the pin to undergo a pulse transition, i.e. from low to high to low again OR from high to low to high again. Returns pulse width when succeeded.
  /// </summary>
  /// <exception cref="InputPinModuleException"></exception>
  public TimeSpan WaitForAnyPulse()
  {
    return CallWithPointer(thisPtr => FfiConverterDuration.INSTANCE.Lift(
_UniffiHelpers.RustCallWithError(FfiConverterTypeInputPinModuleError.INSTANCE, (ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_inputpin_wait_for_any_pulse(thisPtr, ref _status)
)));
  }


  /// <summary>
  /// Wait for the pin to undergo a transition from high to low.
  /// </summary>
  /// <exception cref="InputPinModuleException"></exception>
  public void WaitForFallingEdge()
  {
    CallWithPointer(thisPtr =>
_UniffiHelpers.RustCallWithError(FfiConverterTypeInputPinModuleError.INSTANCE, (ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_inputpin_wait_for_falling_edge(thisPtr, ref _status)
));
  }



  /// <summary>
  /// Wait until the pin is high. If it is already high, return immediately.
  /// </summary>
  /// <exception cref="InputPinModuleException"></exception>
  public void WaitForHigh()
  {
    CallWithPointer(thisPtr =>
_UniffiHelpers.RustCallWithError(FfiConverterTypeInputPinModuleError.INSTANCE, (ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_inputpin_wait_for_high(thisPtr, ref _status)
));
  }



  /// <summary>
  /// Wait for the pin to undergo a pulse transition from low to high to low again. Returns pulse width when succeeded.
  /// </summary>
  /// <exception cref="InputPinModuleException"></exception>
  public TimeSpan WaitForHighPulse()
  {
    return CallWithPointer(thisPtr => FfiConverterDuration.INSTANCE.Lift(
_UniffiHelpers.RustCallWithError(FfiConverterTypeInputPinModuleError.INSTANCE, (ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_inputpin_wait_for_high_pulse(thisPtr, ref _status)
)));
  }


  /// <summary>
  /// Wait until the pin is low. If it is already low, return immediately.
  /// </summary>
  /// <exception cref="InputPinModuleException"></exception>
  public void WaitForLow()
  {
    CallWithPointer(thisPtr =>
_UniffiHelpers.RustCallWithError(FfiConverterTypeInputPinModuleError.INSTANCE, (ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_inputpin_wait_for_low(thisPtr, ref _status)
));
  }



  /// <summary>
  /// Wait for the pin to undergo a pulse transition from high to low to high again. Returns pulse width when succeeded.
  /// </summary>
  /// <exception cref="InputPinModuleException"></exception>
  public TimeSpan WaitForLowPulse()
  {
    return CallWithPointer(thisPtr => FfiConverterDuration.INSTANCE.Lift(
_UniffiHelpers.RustCallWithError(FfiConverterTypeInputPinModuleError.INSTANCE, (ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_inputpin_wait_for_low_pulse(thisPtr, ref _status)
)));
  }


  /// <summary>
  /// Wait for the pin to undergo a transition from low to high.
  /// </summary>
  /// <exception cref="InputPinModuleException"></exception>
  public void WaitForRisingEdge()
  {
    CallWithPointer(thisPtr =>
_UniffiHelpers.RustCallWithError(FfiConverterTypeInputPinModuleError.INSTANCE, (ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_inputpin_wait_for_rising_edge(thisPtr, ref _status)
));
  }





}

class FfiConverterTypeInputPin : FfiConverter<InputPin, IntPtr>
{
  public static FfiConverterTypeInputPin INSTANCE = new FfiConverterTypeInputPin();

  public override IntPtr Lower(InputPin value)
  {
    return value.CallWithPointer(thisPtr => thisPtr);
  }

  public override InputPin Lift(IntPtr value)
  {
    return new InputPin(value);
  }

  public override InputPin Read(BigEndianStream stream)
  {
    return Lift(new IntPtr(stream.ReadLong()));
  }

  public override int AllocationSize(InputPin value)
  {
    return 8;
  }

  public override void Write(InputPin value, BigEndianStream stream)
  {
    stream.WriteLong(Lower(value).ToInt64());
  }
}



public interface IIotzio
{
  ushort ProtocolVersion();
  ulong RuntimeIdentifier();
  String SerialNumber();
  /// <exception cref="I2cBusModuleException"></exception>
  I2cBus SetupI2cBus(I2cConfig @config);
  /// <exception cref="InputPinModuleException"></exception>
  InputPin SetupInputPin(GpioPin @pin, Pull @pullSetting, bool @hysteresis);
  /// <exception cref="OutputPinModuleException"></exception>
  OutputPin SetupOutputPin(GpioPin @pin, Level @initialLevel, Drive @driveStrength, SlewRate @slewRate);
  Version Version();
}
public class Iotzio : FFIObject, IIotzio
{
  public Iotzio(IntPtr pointer) : base(pointer) { }

  protected override void FreeRustArcPtr()
  {
    _UniffiHelpers.RustCall((ref UniffiRustCallStatus status) => {
      _UniFFILib.uniffi_iotzio_fn_free_iotzio(this.pointer, ref status);
    });
  }

  protected override void CloneRustArcPtr()
  {
    _UniffiHelpers.RustCall((ref UniffiRustCallStatus status) => {
      _UniFFILib.uniffi_iotzio_fn_clone_iotzio(this.pointer, ref status);
    });
  }


  public ushort ProtocolVersion()
  {
    return CallWithPointer(thisPtr => FfiConverterUInt16.INSTANCE.Lift(
_UniffiHelpers.RustCall((ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_iotzio_protocol_version(thisPtr, ref _status)
)));
  }


  public ulong RuntimeIdentifier()
  {
    return CallWithPointer(thisPtr => FfiConverterUInt64.INSTANCE.Lift(
_UniffiHelpers.RustCall((ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_iotzio_runtime_identifier(thisPtr, ref _status)
)));
  }


  public String SerialNumber()
  {
    return CallWithPointer(thisPtr => FfiConverterString.INSTANCE.Lift(
_UniffiHelpers.RustCall((ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_iotzio_serial_number(thisPtr, ref _status)
)));
  }


  /// <exception cref="I2cBusModuleException"></exception>
  public I2cBus SetupI2cBus(I2cConfig @config)
  {
    return CallWithPointer(thisPtr => FfiConverterTypeI2cBus.INSTANCE.Lift(
_UniffiHelpers.RustCallWithError(FfiConverterTypeI2cBusModuleError.INSTANCE, (ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_iotzio_setup_i2c_bus(thisPtr, FfiConverterTypeI2cConfig.INSTANCE.Lower(@config), ref _status)
)));
  }


  /// <exception cref="InputPinModuleException"></exception>
  public InputPin SetupInputPin(GpioPin @pin, Pull @pullSetting, bool @hysteresis)
  {
    return CallWithPointer(thisPtr => FfiConverterTypeInputPin.INSTANCE.Lift(
_UniffiHelpers.RustCallWithError(FfiConverterTypeInputPinModuleError.INSTANCE, (ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_iotzio_setup_input_pin(thisPtr, FfiConverterTypeGpioPin.INSTANCE.Lower(@pin), FfiConverterTypePull.INSTANCE.Lower(@pullSetting), FfiConverterBoolean.INSTANCE.Lower(@hysteresis), ref _status)
)));
  }


  /// <exception cref="OutputPinModuleException"></exception>
  public OutputPin SetupOutputPin(GpioPin @pin, Level @initialLevel, Drive @driveStrength, SlewRate @slewRate)
  {
    return CallWithPointer(thisPtr => FfiConverterTypeOutputPin.INSTANCE.Lift(
_UniffiHelpers.RustCallWithError(FfiConverterTypeOutputPinModuleError.INSTANCE, (ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_iotzio_setup_output_pin(thisPtr, FfiConverterTypeGpioPin.INSTANCE.Lower(@pin), FfiConverterTypeLevel.INSTANCE.Lower(@initialLevel), FfiConverterTypeDrive.INSTANCE.Lower(@driveStrength), FfiConverterTypeSlewRate.INSTANCE.Lower(@slewRate), ref _status)
)));
  }


  public Version Version()
  {
    return CallWithPointer(thisPtr => FfiConverterTypeVersion.INSTANCE.Lift(
_UniffiHelpers.RustCall((ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_iotzio_version(thisPtr, ref _status)
)));
  }




}

class FfiConverterTypeIotzio : FfiConverter<Iotzio, IntPtr>
{
  public static FfiConverterTypeIotzio INSTANCE = new FfiConverterTypeIotzio();

  public override IntPtr Lower(Iotzio value)
  {
    return value.CallWithPointer(thisPtr => thisPtr);
  }

  public override Iotzio Lift(IntPtr value)
  {
    return new Iotzio(value);
  }

  public override Iotzio Read(BigEndianStream stream)
  {
    return Lift(new IntPtr(stream.ReadLong()));
  }

  public override int AllocationSize(Iotzio value)
  {
    return 8;
  }

  public override void Write(Iotzio value, BigEndianStream stream)
  {
    stream.WriteLong(Lower(value).ToInt64());
  }
}



public interface IIotzioInfo
{
  /// <exception cref="InitializationException"></exception>
  Iotzio Open();
  ulong RuntimeIdentifier();
  String? SerialNumber();
  Version Version();
}
public class IotzioInfo : FFIObject, IIotzioInfo
{
  public IotzioInfo(IntPtr pointer) : base(pointer) { }

  protected override void FreeRustArcPtr()
  {
    _UniffiHelpers.RustCall((ref UniffiRustCallStatus status) => {
      _UniFFILib.uniffi_iotzio_fn_free_iotzioinfo(this.pointer, ref status);
    });
  }

  protected override void CloneRustArcPtr()
  {
    _UniffiHelpers.RustCall((ref UniffiRustCallStatus status) => {
      _UniFFILib.uniffi_iotzio_fn_clone_iotzioinfo(this.pointer, ref status);
    });
  }


  /// <exception cref="InitializationException"></exception>
  public Iotzio Open()
  {
    return CallWithPointer(thisPtr => FfiConverterTypeIotzio.INSTANCE.Lift(
_UniffiHelpers.RustCallWithError(FfiConverterTypeInitializationError.INSTANCE, (ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_iotzioinfo_open(thisPtr, ref _status)
)));
  }


  public ulong RuntimeIdentifier()
  {
    return CallWithPointer(thisPtr => FfiConverterUInt64.INSTANCE.Lift(
_UniffiHelpers.RustCall((ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_iotzioinfo_runtime_identifier(thisPtr, ref _status)
)));
  }


  public String? SerialNumber()
  {
    return CallWithPointer(thisPtr => FfiConverterOptionalString.INSTANCE.Lift(
_UniffiHelpers.RustCall((ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_iotzioinfo_serial_number(thisPtr, ref _status)
)));
  }


  public Version Version()
  {
    return CallWithPointer(thisPtr => FfiConverterTypeVersion.INSTANCE.Lift(
_UniffiHelpers.RustCall((ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_iotzioinfo_version(thisPtr, ref _status)
)));
  }




}

class FfiConverterTypeIotzioInfo : FfiConverter<IotzioInfo, IntPtr>
{
  public static FfiConverterTypeIotzioInfo INSTANCE = new FfiConverterTypeIotzioInfo();

  public override IntPtr Lower(IotzioInfo value)
  {
    return value.CallWithPointer(thisPtr => thisPtr);
  }

  public override IotzioInfo Lift(IntPtr value)
  {
    return new IotzioInfo(value);
  }

  public override IotzioInfo Read(BigEndianStream stream)
  {
    return Lift(new IntPtr(stream.ReadLong()));
  }

  public override int AllocationSize(IotzioInfo value)
  {
    return 8;
  }

  public override void Write(IotzioInfo value, BigEndianStream stream)
  {
    stream.WriteLong(Lower(value).ToInt64());
  }
}



public interface IIotzioManager
{
  /// <exception cref="InitializationException"></exception>
  List<IotzioInfo> ListConnectedBoards();
}
public class IotzioManager : FFIObject, IIotzioManager
{
  public IotzioManager(IntPtr pointer) : base(pointer) { }
  public IotzioManager() :
      this(
  _UniffiHelpers.RustCall((ref UniffiRustCallStatus _status) =>
  _UniFFILib.uniffi_iotzio_fn_constructor_iotziomanager_new(ref _status)
))
  { }

  protected override void FreeRustArcPtr()
  {
    _UniffiHelpers.RustCall((ref UniffiRustCallStatus status) => {
      _UniFFILib.uniffi_iotzio_fn_free_iotziomanager(this.pointer, ref status);
    });
  }

  protected override void CloneRustArcPtr()
  {
    _UniffiHelpers.RustCall((ref UniffiRustCallStatus status) => {
      _UniFFILib.uniffi_iotzio_fn_clone_iotziomanager(this.pointer, ref status);
    });
  }


  /// <exception cref="InitializationException"></exception>
  public List<IotzioInfo> ListConnectedBoards()
  {
    return CallWithPointer(thisPtr => FfiConverterSequenceTypeIotzioInfo.INSTANCE.Lift(
_UniffiHelpers.RustCallWithError(FfiConverterTypeInitializationError.INSTANCE, (ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_iotziomanager_list_connected_boards(thisPtr, ref _status)
)));
  }




}

class FfiConverterTypeIotzioManager : FfiConverter<IotzioManager, IntPtr>
{
  public static FfiConverterTypeIotzioManager INSTANCE = new FfiConverterTypeIotzioManager();

  public override IntPtr Lower(IotzioManager value)
  {
    return value.CallWithPointer(thisPtr => thisPtr);
  }

  public override IotzioManager Lift(IntPtr value)
  {
    return new IotzioManager(value);
  }

  public override IotzioManager Read(BigEndianStream stream)
  {
    return Lift(new IntPtr(stream.ReadLong()));
  }

  public override int AllocationSize(IotzioManager value)
  {
    return 8;
  }

  public override void Write(IotzioManager value, BigEndianStream stream)
  {
    stream.WriteLong(Lower(value).ToInt64());
  }
}



public interface IOutputPin
{
  /// <summary>
  /// Returns the pin's drive strength.
  /// </summary>
  Drive GetDriveStrength();
  /// <summary>
  /// Returns current pin level.
  /// </summary>
  Level GetLevel();
  /// <summary>
  /// Returns used pin.
  /// </summary>
  GpioPin GetPin();
  /// <summary>
  /// Returns the pin's slew rate.
  /// </summary>
  SlewRate GetSlewRate();
  /// <summary>
  /// Sets current pin level.
  /// </summary>
  /// <exception cref="OutputPinModuleException"></exception>
  void SetLevel(Level @level);
}
public class OutputPin : FFIObject, IOutputPin
{
  public OutputPin(IntPtr pointer) : base(pointer) { }

  protected override void FreeRustArcPtr()
  {
    _UniffiHelpers.RustCall((ref UniffiRustCallStatus status) => {
      _UniFFILib.uniffi_iotzio_fn_free_outputpin(this.pointer, ref status);
    });
  }

  protected override void CloneRustArcPtr()
  {
    _UniffiHelpers.RustCall((ref UniffiRustCallStatus status) => {
      _UniFFILib.uniffi_iotzio_fn_clone_outputpin(this.pointer, ref status);
    });
  }


  /// <summary>
  /// Returns the pin's drive strength.
  /// </summary>
  public Drive GetDriveStrength()
  {
    return CallWithPointer(thisPtr => FfiConverterTypeDrive.INSTANCE.Lift(
_UniffiHelpers.RustCall((ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_outputpin_get_drive_strength(thisPtr, ref _status)
)));
  }


  /// <summary>
  /// Returns current pin level.
  /// </summary>
  public Level GetLevel()
  {
    return CallWithPointer(thisPtr => FfiConverterTypeLevel.INSTANCE.Lift(
_UniffiHelpers.RustCall((ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_outputpin_get_level(thisPtr, ref _status)
)));
  }


  /// <summary>
  /// Returns used pin.
  /// </summary>
  public GpioPin GetPin()
  {
    return CallWithPointer(thisPtr => FfiConverterTypeGpioPin.INSTANCE.Lift(
_UniffiHelpers.RustCall((ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_outputpin_get_pin(thisPtr, ref _status)
)));
  }


  /// <summary>
  /// Returns the pin's slew rate.
  /// </summary>
  public SlewRate GetSlewRate()
  {
    return CallWithPointer(thisPtr => FfiConverterTypeSlewRate.INSTANCE.Lift(
_UniffiHelpers.RustCall((ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_outputpin_get_slew_rate(thisPtr, ref _status)
)));
  }


  /// <summary>
  /// Sets current pin level.
  /// </summary>
  /// <exception cref="OutputPinModuleException"></exception>
  public void SetLevel(Level @level)
  {
    CallWithPointer(thisPtr =>
_UniffiHelpers.RustCallWithError(FfiConverterTypeOutputPinModuleError.INSTANCE, (ref UniffiRustCallStatus _status) =>
_UniFFILib.uniffi_iotzio_fn_method_outputpin_set_level(thisPtr, FfiConverterTypeLevel.INSTANCE.Lower(@level), ref _status)
));
  }





}

class FfiConverterTypeOutputPin : FfiConverter<OutputPin, IntPtr>
{
  public static FfiConverterTypeOutputPin INSTANCE = new FfiConverterTypeOutputPin();

  public override IntPtr Lower(OutputPin value)
  {
    return value.CallWithPointer(thisPtr => thisPtr);
  }

  public override OutputPin Lift(IntPtr value)
  {
    return new OutputPin(value);
  }

  public override OutputPin Read(BigEndianStream stream)
  {
    return Lift(new IntPtr(stream.ReadLong()));
  }

  public override int AllocationSize(OutputPin value)
  {
    return 8;
  }

  public override void Write(OutputPin value, BigEndianStream stream)
  {
    stream.WriteLong(Lower(value).ToInt64());
  }
}



public record BoardInfo(
    Version @version,
    ushort @protocolVersion,
    String @serialNumber
)
{
}

class FfiConverterTypeBoardInfo : FfiConverterRustBuffer<BoardInfo>
{
  public static FfiConverterTypeBoardInfo INSTANCE = new FfiConverterTypeBoardInfo();

  public override BoardInfo Read(BigEndianStream stream)
  {
    return new BoardInfo(
        @version: FfiConverterTypeVersion.INSTANCE.Read(stream),
        @protocolVersion: FfiConverterUInt16.INSTANCE.Read(stream),
        @serialNumber: FfiConverterString.INSTANCE.Read(stream)
    );
  }

  public override int AllocationSize(BoardInfo value)
  {
    return 0
        + FfiConverterTypeVersion.INSTANCE.AllocationSize(value.@version)
        + FfiConverterUInt16.INSTANCE.AllocationSize(value.@protocolVersion)
        + FfiConverterString.INSTANCE.AllocationSize(value.@serialNumber);
  }

  public override void Write(BoardInfo value, BigEndianStream stream)
  {
    FfiConverterTypeVersion.INSTANCE.Write(value.@version, stream);
    FfiConverterUInt16.INSTANCE.Write(value.@protocolVersion, stream);
    FfiConverterString.INSTANCE.Write(value.@serialNumber, stream);
  }
}



public record Version(
    ushort @major,
    ushort @minor,
    ushort @patch
)
{
}

class FfiConverterTypeVersion : FfiConverterRustBuffer<Version>
{
  public static FfiConverterTypeVersion INSTANCE = new FfiConverterTypeVersion();

  public override Version Read(BigEndianStream stream)
  {
    return new Version(
        @major: FfiConverterUInt16.INSTANCE.Read(stream),
        @minor: FfiConverterUInt16.INSTANCE.Read(stream),
        @patch: FfiConverterUInt16.INSTANCE.Read(stream)
    );
  }

  public override int AllocationSize(Version value)
  {
    return 0
        + FfiConverterUInt16.INSTANCE.AllocationSize(value.@major)
        + FfiConverterUInt16.INSTANCE.AllocationSize(value.@minor)
        + FfiConverterUInt16.INSTANCE.AllocationSize(value.@patch);
  }

  public override void Write(Version value, BigEndianStream stream)
  {
    FfiConverterUInt16.INSTANCE.Write(value.@major, stream);
    FfiConverterUInt16.INSTANCE.Write(value.@minor, stream);
    FfiConverterUInt16.INSTANCE.Write(value.@patch, stream);
  }
}





/// <summary>
/// Drive strength of an output
/// </summary>
public enum Drive : int
{

  /// <summary>
  /// 2 mA drive.
  /// </summary>
  TwoMilliAmpere,
  /// <summary>
  /// 4 mA drive.
  /// </summary>
  FourMilliAmpere,
  /// <summary>
  /// 8 mA drive.
  /// </summary>
  EightMilliAmpere,
  /// <summary>
  /// 12 mA drive.
  /// </summary>
  TwelveMilliAmpere
}

class FfiConverterTypeDrive : FfiConverterRustBuffer<Drive>
{
  public static FfiConverterTypeDrive INSTANCE = new FfiConverterTypeDrive();

  public override Drive Read(BigEndianStream stream)
  {
    var value = stream.ReadInt() - 1;
    if (Enum.IsDefined(typeof(Drive), value))
    {
      return (Drive)value;
    }
    else
    {
      throw new InternalException(String.Format("invalid enum value '{0}' in FfiConverterTypeDrive.Read()", value));
    }
  }

  public override int AllocationSize(Drive value)
  {
    return 4;
  }

  public override void Write(Drive value, BigEndianStream stream)
  {
    stream.WriteInt((int)value + 1);
  }
}







public class FatalException : UniffiException
{
  // Each variant is a nested class


  public class HostWriteException : FatalException
  {
    // Members
    public String @errorMessage;

    // Constructor
    public HostWriteException(
            String @errorMessage)
    {
      this.@errorMessage = @errorMessage;
    }
  }


  public class HostReadException : FatalException
  {
    // Members
    public String @errorMessage;

    // Constructor
    public HostReadException(
            String @errorMessage)
    {
      this.@errorMessage = @errorMessage;
    }
  }


  public class HostProtocolException : FatalException
  {
    // Members
    public ProtocolException @error;

    // Constructor
    public HostProtocolException(
            ProtocolException @error)
    {
      this.@error = @error;
    }
  }


  public class DeviceWriteException : FatalException
  {
    // Members
    public String @errorMessage;

    // Constructor
    public DeviceWriteException(
            String @errorMessage)
    {
      this.@errorMessage = @errorMessage;
    }
  }


  public class DeviceReadException : FatalException
  {
    // Members
    public String @errorMessage;

    // Constructor
    public DeviceReadException(
            String @errorMessage)
    {
      this.@errorMessage = @errorMessage;
    }
  }


  public class DeviceProtocolException : FatalException
  {
    // Members
    public ProtocolException @error;

    // Constructor
    public DeviceProtocolException(
            ProtocolException @error)
    {
      this.@error = @error;
    }
  }

  public class DeviceClosed : FatalException { }




}

class FfiConverterTypeFatalError : FfiConverterRustBuffer<FatalException>, CallStatusErrorHandler<FatalException>
{
  public static FfiConverterTypeFatalError INSTANCE = new FfiConverterTypeFatalError();

  public override FatalException Read(BigEndianStream stream)
  {
    var value = stream.ReadInt();
    switch (value)
    {
      case 1:
        return new FatalException.HostWriteException(
            FfiConverterString.INSTANCE.Read(stream));
      case 2:
        return new FatalException.HostReadException(
            FfiConverterString.INSTANCE.Read(stream));
      case 3:
        return new FatalException.HostProtocolException(
            FfiConverterTypeProtocolError.INSTANCE.Read(stream));
      case 4:
        return new FatalException.DeviceWriteException(
            FfiConverterString.INSTANCE.Read(stream));
      case 5:
        return new FatalException.DeviceReadException(
            FfiConverterString.INSTANCE.Read(stream));
      case 6:
        return new FatalException.DeviceProtocolException(
            FfiConverterTypeProtocolError.INSTANCE.Read(stream));
      case 7:
        return new FatalException.DeviceClosed();
      default:
        throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeFatalError.Read()", value));
    }
  }

  public override int AllocationSize(FatalException value)
  {
    switch (value)
    {

      case FatalException.HostWriteException variant_value:
        return 4
            + FfiConverterString.INSTANCE.AllocationSize(variant_value.@errorMessage);

      case FatalException.HostReadException variant_value:
        return 4
            + FfiConverterString.INSTANCE.AllocationSize(variant_value.@errorMessage);

      case FatalException.HostProtocolException variant_value:
        return 4
            + FfiConverterTypeProtocolError.INSTANCE.AllocationSize(variant_value.@error);

      case FatalException.DeviceWriteException variant_value:
        return 4
            + FfiConverterString.INSTANCE.AllocationSize(variant_value.@errorMessage);

      case FatalException.DeviceReadException variant_value:
        return 4
            + FfiConverterString.INSTANCE.AllocationSize(variant_value.@errorMessage);

      case FatalException.DeviceProtocolException variant_value:
        return 4
            + FfiConverterTypeProtocolError.INSTANCE.AllocationSize(variant_value.@error);

      case FatalException.DeviceClosed variant_value:
        return 4;
      default:
        throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeFatalError.AllocationSize()", value));
    }
  }

  public override void Write(FatalException value, BigEndianStream stream)
  {
    switch (value)
    {
      case FatalException.HostWriteException variant_value:
        stream.WriteInt(1);
        FfiConverterString.INSTANCE.Write(variant_value.@errorMessage, stream);
        break;
      case FatalException.HostReadException variant_value:
        stream.WriteInt(2);
        FfiConverterString.INSTANCE.Write(variant_value.@errorMessage, stream);
        break;
      case FatalException.HostProtocolException variant_value:
        stream.WriteInt(3);
        FfiConverterTypeProtocolError.INSTANCE.Write(variant_value.@error, stream);
        break;
      case FatalException.DeviceWriteException variant_value:
        stream.WriteInt(4);
        FfiConverterString.INSTANCE.Write(variant_value.@errorMessage, stream);
        break;
      case FatalException.DeviceReadException variant_value:
        stream.WriteInt(5);
        FfiConverterString.INSTANCE.Write(variant_value.@errorMessage, stream);
        break;
      case FatalException.DeviceProtocolException variant_value:
        stream.WriteInt(6);
        FfiConverterTypeProtocolError.INSTANCE.Write(variant_value.@error, stream);
        break;
      case FatalException.DeviceClosed variant_value:
        stream.WriteInt(7);
        break;
      default:
        throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeFatalError.Write()", value));
    }
  }
}





/// <summary>
/// Number of a pin suitable for GPIO and PWM.
/// </summary>
public enum GpioPin : int
{

  Pin0,
  Pin1,
  Pin2,
  Pin3,
  Pin4,
  Pin5,
  Pin6,
  Pin7,
  Pin8,
  Pin9,
  Pin10,
  Pin11,
  Pin12,
  Pin13,
  Pin14,
  Pin15,
  Pin16,
  Pin17,
  Pin18,
  Pin19,
  Pin20,
  Pin21,
  Pin22,
  Pin25,
  Pin26,
  Pin27,
  Pin28
}

class FfiConverterTypeGpioPin : FfiConverterRustBuffer<GpioPin>
{
  public static FfiConverterTypeGpioPin INSTANCE = new FfiConverterTypeGpioPin();

  public override GpioPin Read(BigEndianStream stream)
  {
    var value = stream.ReadInt() - 1;
    if (Enum.IsDefined(typeof(GpioPin), value))
    {
      return (GpioPin)value;
    }
    else
    {
      throw new InternalException(String.Format("invalid enum value '{0}' in FfiConverterTypeGpioPin.Read()", value));
    }
  }

  public override int AllocationSize(GpioPin value)
  {
    return 4;
  }

  public override void Write(GpioPin value, BigEndianStream stream)
  {
    stream.WriteInt((int)value + 1);
  }
}







/// <summary>
/// Number of a pin suitable for SCL using I2C0.
/// </summary>
public enum I2c0SclPin : int
{

  Pin1,
  Pin5,
  Pin9,
  Pin13,
  Pin17,
  Pin21
}

class FfiConverterTypeI2c0SclPin : FfiConverterRustBuffer<I2c0SclPin>
{
  public static FfiConverterTypeI2c0SclPin INSTANCE = new FfiConverterTypeI2c0SclPin();

  public override I2c0SclPin Read(BigEndianStream stream)
  {
    var value = stream.ReadInt() - 1;
    if (Enum.IsDefined(typeof(I2c0SclPin), value))
    {
      return (I2c0SclPin)value;
    }
    else
    {
      throw new InternalException(String.Format("invalid enum value '{0}' in FfiConverterTypeI2c0SclPin.Read()", value));
    }
  }

  public override int AllocationSize(I2c0SclPin value)
  {
    return 4;
  }

  public override void Write(I2c0SclPin value, BigEndianStream stream)
  {
    stream.WriteInt((int)value + 1);
  }
}







/// <summary>
/// Number of a pin suitable for SDA using I2C0.
/// </summary>
public enum I2c0SdaPin : int
{

  Pin0,
  Pin4,
  Pin8,
  Pin12,
  Pin16,
  Pin20
}

class FfiConverterTypeI2c0SdaPin : FfiConverterRustBuffer<I2c0SdaPin>
{
  public static FfiConverterTypeI2c0SdaPin INSTANCE = new FfiConverterTypeI2c0SdaPin();

  public override I2c0SdaPin Read(BigEndianStream stream)
  {
    var value = stream.ReadInt() - 1;
    if (Enum.IsDefined(typeof(I2c0SdaPin), value))
    {
      return (I2c0SdaPin)value;
    }
    else
    {
      throw new InternalException(String.Format("invalid enum value '{0}' in FfiConverterTypeI2c0SdaPin.Read()", value));
    }
  }

  public override int AllocationSize(I2c0SdaPin value)
  {
    return 4;
  }

  public override void Write(I2c0SdaPin value, BigEndianStream stream)
  {
    stream.WriteInt((int)value + 1);
  }
}







/// <summary>
/// Number of a pin suitable for SCL using I2C1.
/// </summary>
public enum I2c1SclPin : int
{

  Pin3,
  Pin7,
  Pin11,
  Pin15,
  Pin19,
  Pin27
}

class FfiConverterTypeI2c1SclPin : FfiConverterRustBuffer<I2c1SclPin>
{
  public static FfiConverterTypeI2c1SclPin INSTANCE = new FfiConverterTypeI2c1SclPin();

  public override I2c1SclPin Read(BigEndianStream stream)
  {
    var value = stream.ReadInt() - 1;
    if (Enum.IsDefined(typeof(I2c1SclPin), value))
    {
      return (I2c1SclPin)value;
    }
    else
    {
      throw new InternalException(String.Format("invalid enum value '{0}' in FfiConverterTypeI2c1SclPin.Read()", value));
    }
  }

  public override int AllocationSize(I2c1SclPin value)
  {
    return 4;
  }

  public override void Write(I2c1SclPin value, BigEndianStream stream)
  {
    stream.WriteInt((int)value + 1);
  }
}







/// <summary>
/// Number of a pin suitable for SDA using I2C1.
/// </summary>
public enum I2c1SdaPin : int
{

  Pin2,
  Pin6,
  Pin10,
  Pin14,
  Pin18,
  Pin26
}

class FfiConverterTypeI2c1SdaPin : FfiConverterRustBuffer<I2c1SdaPin>
{
  public static FfiConverterTypeI2c1SdaPin INSTANCE = new FfiConverterTypeI2c1SdaPin();

  public override I2c1SdaPin Read(BigEndianStream stream)
  {
    var value = stream.ReadInt() - 1;
    if (Enum.IsDefined(typeof(I2c1SdaPin), value))
    {
      return (I2c1SdaPin)value;
    }
    else
    {
      throw new InternalException(String.Format("invalid enum value '{0}' in FfiConverterTypeI2c1SdaPin.Read()", value));
    }
  }

  public override int AllocationSize(I2c1SdaPin value)
  {
    return 4;
  }

  public override void Write(I2c1SdaPin value, BigEndianStream stream)
  {
    stream.WriteInt((int)value + 1);
  }
}







/// <summary>
/// I2C module error
/// </summary>
public class I2cBusModuleException : UniffiException
{
  // Each variant is a nested class

  public class FrequencyTooHigh : I2cBusModuleException { }


  public class FrequencyTooLow : I2cBusModuleException { }



  public class I2cBusErrorWrapper : I2cBusModuleException
  {
    // Members
    public I2cException @error;

    // Constructor
    public I2cBusErrorWrapper(
            I2cException @error)
    {
      this.@error = @error;
    }
  }


  public class ModuleErrorWrapper : I2cBusModuleException
  {
    // Members
    public ModuleException @error;

    // Constructor
    public ModuleErrorWrapper(
            ModuleException @error)
    {
      this.@error = @error;
    }
  }


  public class FatalErrorWrapper : I2cBusModuleException
  {
    // Members
    public FatalException @error;

    // Constructor
    public FatalErrorWrapper(
            FatalException @error)
    {
      this.@error = @error;
    }
  }



}

class FfiConverterTypeI2cBusModuleError : FfiConverterRustBuffer<I2cBusModuleException>, CallStatusErrorHandler<I2cBusModuleException>
{
  public static FfiConverterTypeI2cBusModuleError INSTANCE = new FfiConverterTypeI2cBusModuleError();

  public override I2cBusModuleException Read(BigEndianStream stream)
  {
    var value = stream.ReadInt();
    switch (value)
    {
      case 1:
        return new I2cBusModuleException.FrequencyTooHigh();
      case 2:
        return new I2cBusModuleException.FrequencyTooLow();
      case 3:
        return new I2cBusModuleException.I2cBusErrorWrapper(
            FfiConverterTypeI2cError.INSTANCE.Read(stream));
      case 4:
        return new I2cBusModuleException.ModuleErrorWrapper(
            FfiConverterTypeModuleError.INSTANCE.Read(stream));
      case 5:
        return new I2cBusModuleException.FatalErrorWrapper(
            FfiConverterTypeFatalError.INSTANCE.Read(stream));
      default:
        throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeI2cBusModuleError.Read()", value));
    }
  }

  public override int AllocationSize(I2cBusModuleException value)
  {
    switch (value)
    {

      case I2cBusModuleException.FrequencyTooHigh variant_value:
        return 4;

      case I2cBusModuleException.FrequencyTooLow variant_value:
        return 4;

      case I2cBusModuleException.I2cBusErrorWrapper variant_value:
        return 4
            + FfiConverterTypeI2cError.INSTANCE.AllocationSize(variant_value.@error);

      case I2cBusModuleException.ModuleErrorWrapper variant_value:
        return 4
            + FfiConverterTypeModuleError.INSTANCE.AllocationSize(variant_value.@error);

      case I2cBusModuleException.FatalErrorWrapper variant_value:
        return 4
            + FfiConverterTypeFatalError.INSTANCE.AllocationSize(variant_value.@error);
      default:
        throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeI2cBusModuleError.AllocationSize()", value));
    }
  }

  public override void Write(I2cBusModuleException value, BigEndianStream stream)
  {
    switch (value)
    {
      case I2cBusModuleException.FrequencyTooHigh variant_value:
        stream.WriteInt(1);
        break;
      case I2cBusModuleException.FrequencyTooLow variant_value:
        stream.WriteInt(2);
        break;
      case I2cBusModuleException.I2cBusErrorWrapper variant_value:
        stream.WriteInt(3);
        FfiConverterTypeI2cError.INSTANCE.Write(variant_value.@error, stream);
        break;
      case I2cBusModuleException.ModuleErrorWrapper variant_value:
        stream.WriteInt(4);
        FfiConverterTypeModuleError.INSTANCE.Write(variant_value.@error, stream);
        break;
      case I2cBusModuleException.FatalErrorWrapper variant_value:
        stream.WriteInt(5);
        FfiConverterTypeFatalError.INSTANCE.Write(variant_value.@error, stream);
        break;
      default:
        throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeI2cBusModuleError.Write()", value));
    }
  }
}





public record I2cConfig
{

  public record I2c0(
      I2c0SclPin @scl,
      I2c0SdaPin @sda,
      uint? @requestedFrequencyHz
  ) : I2cConfig
  { }

  public record I2c1(
      I2c1SclPin @scl,
      I2c1SdaPin @sda,
      uint? @requestedFrequencyHz
  ) : I2cConfig
  { }



}

class FfiConverterTypeI2cConfig : FfiConverterRustBuffer<I2cConfig>
{
  public static FfiConverterRustBuffer<I2cConfig> INSTANCE = new FfiConverterTypeI2cConfig();

  public override I2cConfig Read(BigEndianStream stream)
  {
    var value = stream.ReadInt();
    switch (value)
    {
      case 1:
        return new I2cConfig.I2c0(
            FfiConverterTypeI2c0SclPin.INSTANCE.Read(stream),
            FfiConverterTypeI2c0SdaPin.INSTANCE.Read(stream),
            FfiConverterOptionalUInt32.INSTANCE.Read(stream)
        );
      case 2:
        return new I2cConfig.I2c1(
            FfiConverterTypeI2c1SclPin.INSTANCE.Read(stream),
            FfiConverterTypeI2c1SdaPin.INSTANCE.Read(stream),
            FfiConverterOptionalUInt32.INSTANCE.Read(stream)
        );
      default:
        throw new InternalException(String.Format("invalid enum value '{0}' in FfiConverterTypeI2cConfig.Read()", value));
    }
  }

  public override int AllocationSize(I2cConfig value)
  {
    switch (value)
    {
      case I2cConfig.I2c0 variant_value:
        return 4
            + FfiConverterTypeI2c0SclPin.INSTANCE.AllocationSize(variant_value.@scl)
            + FfiConverterTypeI2c0SdaPin.INSTANCE.AllocationSize(variant_value.@sda)
            + FfiConverterOptionalUInt32.INSTANCE.AllocationSize(variant_value.@requestedFrequencyHz);
      case I2cConfig.I2c1 variant_value:
        return 4
            + FfiConverterTypeI2c1SclPin.INSTANCE.AllocationSize(variant_value.@scl)
            + FfiConverterTypeI2c1SdaPin.INSTANCE.AllocationSize(variant_value.@sda)
            + FfiConverterOptionalUInt32.INSTANCE.AllocationSize(variant_value.@requestedFrequencyHz);
      default:
        throw new InternalException(String.Format("invalid enum value '{0}' in FfiConverterTypeI2cConfig.AllocationSize()", value));
    }
  }

  public override void Write(I2cConfig value, BigEndianStream stream)
  {
    switch (value)
    {
      case I2cConfig.I2c0 variant_value:
        stream.WriteInt(1);
        FfiConverterTypeI2c0SclPin.INSTANCE.Write(variant_value.@scl, stream);
        FfiConverterTypeI2c0SdaPin.INSTANCE.Write(variant_value.@sda, stream);
        FfiConverterOptionalUInt32.INSTANCE.Write(variant_value.@requestedFrequencyHz, stream);
        break;
      case I2cConfig.I2c1 variant_value:
        stream.WriteInt(2);
        FfiConverterTypeI2c1SclPin.INSTANCE.Write(variant_value.@scl, stream);
        FfiConverterTypeI2c1SdaPin.INSTANCE.Write(variant_value.@sda, stream);
        FfiConverterOptionalUInt32.INSTANCE.Write(variant_value.@requestedFrequencyHz, stream);
        break;
      default:
        throw new InternalException(String.Format("invalid enum value '{0}' in FfiConverterTypeI2cConfig.Write()", value));
    }
  }
}







/// <summary>
/// I2C bus error.
/// </summary>
public class I2cException : UniffiException
{
  // Each variant is a nested class

  public class AbortNoAcknowledge : I2cException { }


  public class AbortArbitrationLoss : I2cException { }



  public class AbortTxNotEmpty : I2cException
  {
    // Members
    public ushort @length;

    // Constructor
    public AbortTxNotEmpty(
            ushort @length)
    {
      this.@length = @length;
    }
  }

  public class AbortOther : I2cException { }


  public class InvalidReadBufferLength : I2cException { }


  public class InvalidWriteBufferLength : I2cException { }



  public class AddressOutOfRange : I2cException
  {
    // Members
    public ushort @address;

    // Constructor
    public AddressOutOfRange(
            ushort @address)
    {
      this.@address = @address;
    }
  }


  public class AddressReserved : I2cException
  {
    // Members
    public ushort @address;

    // Constructor
    public AddressReserved(
            ushort @address)
    {
      this.@address = @address;
    }
  }



}

class FfiConverterTypeI2cError : FfiConverterRustBuffer<I2cException>, CallStatusErrorHandler<I2cException>
{
  public static FfiConverterTypeI2cError INSTANCE = new FfiConverterTypeI2cError();

  public override I2cException Read(BigEndianStream stream)
  {
    var value = stream.ReadInt();
    switch (value)
    {
      case 1:
        return new I2cException.AbortNoAcknowledge();
      case 2:
        return new I2cException.AbortArbitrationLoss();
      case 3:
        return new I2cException.AbortTxNotEmpty(
            FfiConverterUInt16.INSTANCE.Read(stream));
      case 4:
        return new I2cException.AbortOther();
      case 5:
        return new I2cException.InvalidReadBufferLength();
      case 6:
        return new I2cException.InvalidWriteBufferLength();
      case 7:
        return new I2cException.AddressOutOfRange(
            FfiConverterUInt16.INSTANCE.Read(stream));
      case 8:
        return new I2cException.AddressReserved(
            FfiConverterUInt16.INSTANCE.Read(stream));
      default:
        throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeI2cError.Read()", value));
    }
  }

  public override int AllocationSize(I2cException value)
  {
    switch (value)
    {

      case I2cException.AbortNoAcknowledge variant_value:
        return 4;

      case I2cException.AbortArbitrationLoss variant_value:
        return 4;

      case I2cException.AbortTxNotEmpty variant_value:
        return 4
            + FfiConverterUInt16.INSTANCE.AllocationSize(variant_value.@length);

      case I2cException.AbortOther variant_value:
        return 4;

      case I2cException.InvalidReadBufferLength variant_value:
        return 4;

      case I2cException.InvalidWriteBufferLength variant_value:
        return 4;

      case I2cException.AddressOutOfRange variant_value:
        return 4
            + FfiConverterUInt16.INSTANCE.AllocationSize(variant_value.@address);

      case I2cException.AddressReserved variant_value:
        return 4
            + FfiConverterUInt16.INSTANCE.AllocationSize(variant_value.@address);
      default:
        throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeI2cError.AllocationSize()", value));
    }
  }

  public override void Write(I2cException value, BigEndianStream stream)
  {
    switch (value)
    {
      case I2cException.AbortNoAcknowledge variant_value:
        stream.WriteInt(1);
        break;
      case I2cException.AbortArbitrationLoss variant_value:
        stream.WriteInt(2);
        break;
      case I2cException.AbortTxNotEmpty variant_value:
        stream.WriteInt(3);
        FfiConverterUInt16.INSTANCE.Write(variant_value.@length, stream);
        break;
      case I2cException.AbortOther variant_value:
        stream.WriteInt(4);
        break;
      case I2cException.InvalidReadBufferLength variant_value:
        stream.WriteInt(5);
        break;
      case I2cException.InvalidWriteBufferLength variant_value:
        stream.WriteInt(6);
        break;
      case I2cException.AddressOutOfRange variant_value:
        stream.WriteInt(7);
        FfiConverterUInt16.INSTANCE.Write(variant_value.@address, stream);
        break;
      case I2cException.AddressReserved variant_value:
        stream.WriteInt(8);
        FfiConverterUInt16.INSTANCE.Write(variant_value.@address, stream);
        break;
      default:
        throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeI2cError.Write()", value));
    }
  }
}





/// <summary>
/// Represents an I2C bus.
/// </summary>
public enum I2cIdentifier : int
{

  /// <summary>
  /// I2C Bus 0
  /// </summary>
  I2c0,
  /// <summary>
  /// I2C Bus 1
  /// </summary>
  I2c1
}

class FfiConverterTypeI2cIdentifier : FfiConverterRustBuffer<I2cIdentifier>
{
  public static FfiConverterTypeI2cIdentifier INSTANCE = new FfiConverterTypeI2cIdentifier();

  public override I2cIdentifier Read(BigEndianStream stream)
  {
    var value = stream.ReadInt() - 1;
    if (Enum.IsDefined(typeof(I2cIdentifier), value))
    {
      return (I2cIdentifier)value;
    }
    else
    {
      throw new InternalException(String.Format("invalid enum value '{0}' in FfiConverterTypeI2cIdentifier.Read()", value));
    }
  }

  public override int AllocationSize(I2cIdentifier value)
  {
    return 4;
  }

  public override void Write(I2cIdentifier value, BigEndianStream stream)
  {
    stream.WriteInt((int)value + 1);
  }
}







public class InitializationException : UniffiException
{
  // Each variant is a nested class

  public class DeviceAlreadyInUseException : InitializationException { }



  public class DeviceOpenException : InitializationException
  {
    // Members
    public String @errorMessage;

    // Constructor
    public DeviceOpenException(
            String @errorMessage)
    {
      this.@errorMessage = @errorMessage;
    }
  }


  public class MismatchingProtocolVersion : InitializationException
  {
    // Members
    public ushort @driver;
    public ushort @board;

    // Constructor
    public MismatchingProtocolVersion(
            ushort @driver,
            ushort @board)
    {
      this.@driver = @driver;
      this.@board = @board;
    }
  }


  public class FatalErrorWrapper : InitializationException
  {
    // Members
    public FatalException @error;

    // Constructor
    public FatalErrorWrapper(
            FatalException @error)
    {
      this.@error = @error;
    }
  }



}

class FfiConverterTypeInitializationError : FfiConverterRustBuffer<InitializationException>, CallStatusErrorHandler<InitializationException>
{
  public static FfiConverterTypeInitializationError INSTANCE = new FfiConverterTypeInitializationError();

  public override InitializationException Read(BigEndianStream stream)
  {
    var value = stream.ReadInt();
    switch (value)
    {
      case 1:
        return new InitializationException.DeviceAlreadyInUseException();
      case 2:
        return new InitializationException.DeviceOpenException(
            FfiConverterString.INSTANCE.Read(stream));
      case 3:
        return new InitializationException.MismatchingProtocolVersion(
            FfiConverterUInt16.INSTANCE.Read(stream),
            FfiConverterUInt16.INSTANCE.Read(stream));
      case 4:
        return new InitializationException.FatalErrorWrapper(
            FfiConverterTypeFatalError.INSTANCE.Read(stream));
      default:
        throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeInitializationError.Read()", value));
    }
  }

  public override int AllocationSize(InitializationException value)
  {
    switch (value)
    {

      case InitializationException.DeviceAlreadyInUseException variant_value:
        return 4;

      case InitializationException.DeviceOpenException variant_value:
        return 4
            + FfiConverterString.INSTANCE.AllocationSize(variant_value.@errorMessage);

      case InitializationException.MismatchingProtocolVersion variant_value:
        return 4
            + FfiConverterUInt16.INSTANCE.AllocationSize(variant_value.@driver)
            + FfiConverterUInt16.INSTANCE.AllocationSize(variant_value.@board);

      case InitializationException.FatalErrorWrapper variant_value:
        return 4
            + FfiConverterTypeFatalError.INSTANCE.AllocationSize(variant_value.@error);
      default:
        throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeInitializationError.AllocationSize()", value));
    }
  }

  public override void Write(InitializationException value, BigEndianStream stream)
  {
    switch (value)
    {
      case InitializationException.DeviceAlreadyInUseException variant_value:
        stream.WriteInt(1);
        break;
      case InitializationException.DeviceOpenException variant_value:
        stream.WriteInt(2);
        FfiConverterString.INSTANCE.Write(variant_value.@errorMessage, stream);
        break;
      case InitializationException.MismatchingProtocolVersion variant_value:
        stream.WriteInt(3);
        FfiConverterUInt16.INSTANCE.Write(variant_value.@driver, stream);
        FfiConverterUInt16.INSTANCE.Write(variant_value.@board, stream);
        break;
      case InitializationException.FatalErrorWrapper variant_value:
        stream.WriteInt(4);
        FfiConverterTypeFatalError.INSTANCE.Write(variant_value.@error, stream);
        break;
      default:
        throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeInitializationError.Write()", value));
    }
  }
}





public class InputPinModuleException : UniffiException
{
  // Each variant is a nested class


  public class ModuleErrorWrapper : InputPinModuleException
  {
    // Members
    public ModuleException @error;

    // Constructor
    public ModuleErrorWrapper(
            ModuleException @error)
    {
      this.@error = @error;
    }
  }


  public class FatalErrorWrapper : InputPinModuleException
  {
    // Members
    public FatalException @error;

    // Constructor
    public FatalErrorWrapper(
            FatalException @error)
    {
      this.@error = @error;
    }
  }



}

class FfiConverterTypeInputPinModuleError : FfiConverterRustBuffer<InputPinModuleException>, CallStatusErrorHandler<InputPinModuleException>
{
  public static FfiConverterTypeInputPinModuleError INSTANCE = new FfiConverterTypeInputPinModuleError();

  public override InputPinModuleException Read(BigEndianStream stream)
  {
    var value = stream.ReadInt();
    switch (value)
    {
      case 1:
        return new InputPinModuleException.ModuleErrorWrapper(
            FfiConverterTypeModuleError.INSTANCE.Read(stream));
      case 2:
        return new InputPinModuleException.FatalErrorWrapper(
            FfiConverterTypeFatalError.INSTANCE.Read(stream));
      default:
        throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeInputPinModuleError.Read()", value));
    }
  }

  public override int AllocationSize(InputPinModuleException value)
  {
    switch (value)
    {

      case InputPinModuleException.ModuleErrorWrapper variant_value:
        return 4
            + FfiConverterTypeModuleError.INSTANCE.AllocationSize(variant_value.@error);

      case InputPinModuleException.FatalErrorWrapper variant_value:
        return 4
            + FfiConverterTypeFatalError.INSTANCE.AllocationSize(variant_value.@error);
      default:
        throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeInputPinModuleError.AllocationSize()", value));
    }
  }

  public override void Write(InputPinModuleException value, BigEndianStream stream)
  {
    switch (value)
    {
      case InputPinModuleException.ModuleErrorWrapper variant_value:
        stream.WriteInt(1);
        FfiConverterTypeModuleError.INSTANCE.Write(variant_value.@error, stream);
        break;
      case InputPinModuleException.FatalErrorWrapper variant_value:
        stream.WriteInt(2);
        FfiConverterTypeFatalError.INSTANCE.Write(variant_value.@error, stream);
        break;
      default:
        throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeInputPinModuleError.Write()", value));
    }
  }
}





public enum InterruptTrigger : int
{

  /// <summary>
  /// Trigger on pin low.
  /// </summary>
  LevelLow,
  /// <summary>
  /// Trigger on pin high.
  /// </summary>
  LevelHigh,
  /// <summary>
  /// Trigger on high to low transition.
  /// </summary>
  EdgeLow,
  /// <summary>
  /// Trigger on low to high transition.
  /// </summary>
  EdgeHigh,
  /// <summary>
  /// Trigger on any transition.
  /// </summary>
  AnyEdge
}

class FfiConverterTypeInterruptTrigger : FfiConverterRustBuffer<InterruptTrigger>
{
  public static FfiConverterTypeInterruptTrigger INSTANCE = new FfiConverterTypeInterruptTrigger();

  public override InterruptTrigger Read(BigEndianStream stream)
  {
    var value = stream.ReadInt() - 1;
    if (Enum.IsDefined(typeof(InterruptTrigger), value))
    {
      return (InterruptTrigger)value;
    }
    else
    {
      throw new InternalException(String.Format("invalid enum value '{0}' in FfiConverterTypeInterruptTrigger.Read()", value));
    }
  }

  public override int AllocationSize(InterruptTrigger value)
  {
    return 4;
  }

  public override void Write(InterruptTrigger value, BigEndianStream stream)
  {
    stream.WriteInt((int)value + 1);
  }
}







/// <summary>
/// Represents a digital input or output level.
/// </summary>
public enum Level : int
{

  /// <summary>
  /// Logical low.
  /// </summary>
  Low,
  /// <summary>
  /// Logical high.
  /// </summary>
  High
}

class FfiConverterTypeLevel : FfiConverterRustBuffer<Level>
{
  public static FfiConverterTypeLevel INSTANCE = new FfiConverterTypeLevel();

  public override Level Read(BigEndianStream stream)
  {
    var value = stream.ReadInt() - 1;
    if (Enum.IsDefined(typeof(Level), value))
    {
      return (Level)value;
    }
    else
    {
      throw new InternalException(String.Format("invalid enum value '{0}' in FfiConverterTypeLevel.Read()", value));
    }
  }

  public override int AllocationSize(Level value)
  {
    return 4;
  }

  public override void Write(Level value, BigEndianStream stream)
  {
    stream.WriteInt((int)value + 1);
  }
}







public class ModuleException : UniffiException
{
  // Each variant is a nested class

  public class UnknownCommand : ModuleException { }


  public class UnlicensedModule : ModuleException { }


  public class PeripheralBlockedByAnotherModule : ModuleException { }


  public class ModuleTaskCancelled : ModuleException { }


  public class ModuleStorageExhausted : ModuleException { }


  public class ModuleInstanceNotFound : ModuleException { }




}

class FfiConverterTypeModuleError : FfiConverterRustBuffer<ModuleException>, CallStatusErrorHandler<ModuleException>
{
  public static FfiConverterTypeModuleError INSTANCE = new FfiConverterTypeModuleError();

  public override ModuleException Read(BigEndianStream stream)
  {
    var value = stream.ReadInt();
    switch (value)
    {
      case 1:
        return new ModuleException.UnknownCommand();
      case 2:
        return new ModuleException.UnlicensedModule();
      case 3:
        return new ModuleException.PeripheralBlockedByAnotherModule();
      case 4:
        return new ModuleException.ModuleTaskCancelled();
      case 5:
        return new ModuleException.ModuleStorageExhausted();
      case 6:
        return new ModuleException.ModuleInstanceNotFound();
      default:
        throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeModuleError.Read()", value));
    }
  }

  public override int AllocationSize(ModuleException value)
  {
    switch (value)
    {

      case ModuleException.UnknownCommand variant_value:
        return 4;

      case ModuleException.UnlicensedModule variant_value:
        return 4;

      case ModuleException.PeripheralBlockedByAnotherModule variant_value:
        return 4;

      case ModuleException.ModuleTaskCancelled variant_value:
        return 4;

      case ModuleException.ModuleStorageExhausted variant_value:
        return 4;

      case ModuleException.ModuleInstanceNotFound variant_value:
        return 4;
      default:
        throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeModuleError.AllocationSize()", value));
    }
  }

  public override void Write(ModuleException value, BigEndianStream stream)
  {
    switch (value)
    {
      case ModuleException.UnknownCommand variant_value:
        stream.WriteInt(1);
        break;
      case ModuleException.UnlicensedModule variant_value:
        stream.WriteInt(2);
        break;
      case ModuleException.PeripheralBlockedByAnotherModule variant_value:
        stream.WriteInt(3);
        break;
      case ModuleException.ModuleTaskCancelled variant_value:
        stream.WriteInt(4);
        break;
      case ModuleException.ModuleStorageExhausted variant_value:
        stream.WriteInt(5);
        break;
      case ModuleException.ModuleInstanceNotFound variant_value:
        stream.WriteInt(6);
        break;
      default:
        throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeModuleError.Write()", value));
    }
  }
}





public class OutputPinModuleException : UniffiException
{
  // Each variant is a nested class


  public class ModuleErrorWrapper : OutputPinModuleException
  {
    // Members
    public ModuleException @error;

    // Constructor
    public ModuleErrorWrapper(
            ModuleException @error)
    {
      this.@error = @error;
    }
  }


  public class FatalErrorWrapper : OutputPinModuleException
  {
    // Members
    public FatalException @error;

    // Constructor
    public FatalErrorWrapper(
            FatalException @error)
    {
      this.@error = @error;
    }
  }



}

class FfiConverterTypeOutputPinModuleError : FfiConverterRustBuffer<OutputPinModuleException>, CallStatusErrorHandler<OutputPinModuleException>
{
  public static FfiConverterTypeOutputPinModuleError INSTANCE = new FfiConverterTypeOutputPinModuleError();

  public override OutputPinModuleException Read(BigEndianStream stream)
  {
    var value = stream.ReadInt();
    switch (value)
    {
      case 1:
        return new OutputPinModuleException.ModuleErrorWrapper(
            FfiConverterTypeModuleError.INSTANCE.Read(stream));
      case 2:
        return new OutputPinModuleException.FatalErrorWrapper(
            FfiConverterTypeFatalError.INSTANCE.Read(stream));
      default:
        throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeOutputPinModuleError.Read()", value));
    }
  }

  public override int AllocationSize(OutputPinModuleException value)
  {
    switch (value)
    {

      case OutputPinModuleException.ModuleErrorWrapper variant_value:
        return 4
            + FfiConverterTypeModuleError.INSTANCE.AllocationSize(variant_value.@error);

      case OutputPinModuleException.FatalErrorWrapper variant_value:
        return 4
            + FfiConverterTypeFatalError.INSTANCE.AllocationSize(variant_value.@error);
      default:
        throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeOutputPinModuleError.AllocationSize()", value));
    }
  }

  public override void Write(OutputPinModuleException value, BigEndianStream stream)
  {
    switch (value)
    {
      case OutputPinModuleException.ModuleErrorWrapper variant_value:
        stream.WriteInt(1);
        FfiConverterTypeModuleError.INSTANCE.Write(variant_value.@error, stream);
        break;
      case OutputPinModuleException.FatalErrorWrapper variant_value:
        stream.WriteInt(2);
        FfiConverterTypeFatalError.INSTANCE.Write(variant_value.@error, stream);
        break;
      default:
        throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeOutputPinModuleError.Write()", value));
    }
  }
}





public class ProtocolException : UniffiException
{
  // Each variant is a nested class

  public class PacketTooSmall : ProtocolException { }


  public class ErrorSelectingReportId : ProtocolException { }


  public class ReceivedWrongResponse : ProtocolException { }


  public class ReceivedImpossibleCommandException : ProtocolException { }


  public class ReceivedImpossibleCommand : ProtocolException { }


  public class WontImplement : ProtocolException { }


  public class NotYetImplemented : ProtocolException { }


  public class SerializeBufferFull : ProtocolException { }


  public class SerializeSeqLengthUnknown : ProtocolException { }


  public class DeserializeUnexpectedEnd : ProtocolException { }


  public class DeserializeBadVarint : ProtocolException { }


  public class DeserializeBadBool : ProtocolException { }


  public class DeserializeBadChar : ProtocolException { }


  public class DeserializeBadUtf8 : ProtocolException { }


  public class DeserializeBadOption : ProtocolException { }


  public class DeserializeBadEnum : ProtocolException { }


  public class DeserializeBadEncoding : ProtocolException { }


  public class DeserializeBadCrc : ProtocolException { }


  public class SerdeSerCustom : ProtocolException { }


  public class SerdeDeCustom : ProtocolException { }


  public class CollectStrException : ProtocolException { }


  public class Unknown : ProtocolException { }




}

class FfiConverterTypeProtocolError : FfiConverterRustBuffer<ProtocolException>, CallStatusErrorHandler<ProtocolException>
{
  public static FfiConverterTypeProtocolError INSTANCE = new FfiConverterTypeProtocolError();

  public override ProtocolException Read(BigEndianStream stream)
  {
    var value = stream.ReadInt();
    switch (value)
    {
      case 1:
        return new ProtocolException.PacketTooSmall();
      case 2:
        return new ProtocolException.ErrorSelectingReportId();
      case 3:
        return new ProtocolException.ReceivedWrongResponse();
      case 4:
        return new ProtocolException.ReceivedImpossibleCommandException();
      case 5:
        return new ProtocolException.ReceivedImpossibleCommand();
      case 6:
        return new ProtocolException.WontImplement();
      case 7:
        return new ProtocolException.NotYetImplemented();
      case 8:
        return new ProtocolException.SerializeBufferFull();
      case 9:
        return new ProtocolException.SerializeSeqLengthUnknown();
      case 10:
        return new ProtocolException.DeserializeUnexpectedEnd();
      case 11:
        return new ProtocolException.DeserializeBadVarint();
      case 12:
        return new ProtocolException.DeserializeBadBool();
      case 13:
        return new ProtocolException.DeserializeBadChar();
      case 14:
        return new ProtocolException.DeserializeBadUtf8();
      case 15:
        return new ProtocolException.DeserializeBadOption();
      case 16:
        return new ProtocolException.DeserializeBadEnum();
      case 17:
        return new ProtocolException.DeserializeBadEncoding();
      case 18:
        return new ProtocolException.DeserializeBadCrc();
      case 19:
        return new ProtocolException.SerdeSerCustom();
      case 20:
        return new ProtocolException.SerdeDeCustom();
      case 21:
        return new ProtocolException.CollectStrException();
      case 22:
        return new ProtocolException.Unknown();
      default:
        throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeProtocolError.Read()", value));
    }
  }

  public override int AllocationSize(ProtocolException value)
  {
    switch (value)
    {

      case ProtocolException.PacketTooSmall variant_value:
        return 4;

      case ProtocolException.ErrorSelectingReportId variant_value:
        return 4;

      case ProtocolException.ReceivedWrongResponse variant_value:
        return 4;

      case ProtocolException.ReceivedImpossibleCommandException variant_value:
        return 4;

      case ProtocolException.ReceivedImpossibleCommand variant_value:
        return 4;

      case ProtocolException.WontImplement variant_value:
        return 4;

      case ProtocolException.NotYetImplemented variant_value:
        return 4;

      case ProtocolException.SerializeBufferFull variant_value:
        return 4;

      case ProtocolException.SerializeSeqLengthUnknown variant_value:
        return 4;

      case ProtocolException.DeserializeUnexpectedEnd variant_value:
        return 4;

      case ProtocolException.DeserializeBadVarint variant_value:
        return 4;

      case ProtocolException.DeserializeBadBool variant_value:
        return 4;

      case ProtocolException.DeserializeBadChar variant_value:
        return 4;

      case ProtocolException.DeserializeBadUtf8 variant_value:
        return 4;

      case ProtocolException.DeserializeBadOption variant_value:
        return 4;

      case ProtocolException.DeserializeBadEnum variant_value:
        return 4;

      case ProtocolException.DeserializeBadEncoding variant_value:
        return 4;

      case ProtocolException.DeserializeBadCrc variant_value:
        return 4;

      case ProtocolException.SerdeSerCustom variant_value:
        return 4;

      case ProtocolException.SerdeDeCustom variant_value:
        return 4;

      case ProtocolException.CollectStrException variant_value:
        return 4;

      case ProtocolException.Unknown variant_value:
        return 4;
      default:
        throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeProtocolError.AllocationSize()", value));
    }
  }

  public override void Write(ProtocolException value, BigEndianStream stream)
  {
    switch (value)
    {
      case ProtocolException.PacketTooSmall variant_value:
        stream.WriteInt(1);
        break;
      case ProtocolException.ErrorSelectingReportId variant_value:
        stream.WriteInt(2);
        break;
      case ProtocolException.ReceivedWrongResponse variant_value:
        stream.WriteInt(3);
        break;
      case ProtocolException.ReceivedImpossibleCommandException variant_value:
        stream.WriteInt(4);
        break;
      case ProtocolException.ReceivedImpossibleCommand variant_value:
        stream.WriteInt(5);
        break;
      case ProtocolException.WontImplement variant_value:
        stream.WriteInt(6);
        break;
      case ProtocolException.NotYetImplemented variant_value:
        stream.WriteInt(7);
        break;
      case ProtocolException.SerializeBufferFull variant_value:
        stream.WriteInt(8);
        break;
      case ProtocolException.SerializeSeqLengthUnknown variant_value:
        stream.WriteInt(9);
        break;
      case ProtocolException.DeserializeUnexpectedEnd variant_value:
        stream.WriteInt(10);
        break;
      case ProtocolException.DeserializeBadVarint variant_value:
        stream.WriteInt(11);
        break;
      case ProtocolException.DeserializeBadBool variant_value:
        stream.WriteInt(12);
        break;
      case ProtocolException.DeserializeBadChar variant_value:
        stream.WriteInt(13);
        break;
      case ProtocolException.DeserializeBadUtf8 variant_value:
        stream.WriteInt(14);
        break;
      case ProtocolException.DeserializeBadOption variant_value:
        stream.WriteInt(15);
        break;
      case ProtocolException.DeserializeBadEnum variant_value:
        stream.WriteInt(16);
        break;
      case ProtocolException.DeserializeBadEncoding variant_value:
        stream.WriteInt(17);
        break;
      case ProtocolException.DeserializeBadCrc variant_value:
        stream.WriteInt(18);
        break;
      case ProtocolException.SerdeSerCustom variant_value:
        stream.WriteInt(19);
        break;
      case ProtocolException.SerdeDeCustom variant_value:
        stream.WriteInt(20);
        break;
      case ProtocolException.CollectStrException variant_value:
        stream.WriteInt(21);
        break;
      case ProtocolException.Unknown variant_value:
        stream.WriteInt(22);
        break;
      default:
        throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeProtocolError.Write()", value));
    }
  }
}





/// <summary>
/// Represents a pull setting for an input.
/// </summary>
public enum Pull : int
{

  /// <summary>
  /// No pull.
  /// </summary>
  None,
  /// <summary>
  /// Internal pull-up resistor.
  /// </summary>
  Up,
  /// <summary>
  /// Internal pull-down resistor.
  /// </summary>
  Down
}

class FfiConverterTypePull : FfiConverterRustBuffer<Pull>
{
  public static FfiConverterTypePull INSTANCE = new FfiConverterTypePull();

  public override Pull Read(BigEndianStream stream)
  {
    var value = stream.ReadInt() - 1;
    if (Enum.IsDefined(typeof(Pull), value))
    {
      return (Pull)value;
    }
    else
    {
      throw new InternalException(String.Format("invalid enum value '{0}' in FfiConverterTypePull.Read()", value));
    }
  }

  public override int AllocationSize(Pull value)
  {
    return 4;
  }

  public override void Write(Pull value, BigEndianStream stream)
  {
    stream.WriteInt((int)value + 1);
  }
}







/// <summary>
/// Slew rate of an output.
/// </summary>
public enum SlewRate : int
{

  /// <summary>
  /// Fast slew rate.
  /// </summary>
  Fast,
  /// <summary>
  /// Slow slew rate.
  /// </summary>
  Slow
}

class FfiConverterTypeSlewRate : FfiConverterRustBuffer<SlewRate>
{
  public static FfiConverterTypeSlewRate INSTANCE = new FfiConverterTypeSlewRate();

  public override SlewRate Read(BigEndianStream stream)
  {
    var value = stream.ReadInt() - 1;
    if (Enum.IsDefined(typeof(SlewRate), value))
    {
      return (SlewRate)value;
    }
    else
    {
      throw new InternalException(String.Format("invalid enum value '{0}' in FfiConverterTypeSlewRate.Read()", value));
    }
  }

  public override int AllocationSize(SlewRate value)
  {
    return 4;
  }

  public override void Write(SlewRate value, BigEndianStream stream)
  {
    stream.WriteInt((int)value + 1);
  }
}






class FfiConverterOptionalUInt32 : FfiConverterRustBuffer<uint?>
{
  public static FfiConverterOptionalUInt32 INSTANCE = new FfiConverterOptionalUInt32();

  public override uint? Read(BigEndianStream stream)
  {
    if (stream.ReadByte() == 0)
    {
      return null;
    }
    return FfiConverterUInt32.INSTANCE.Read(stream);
  }

  public override int AllocationSize(uint? value)
  {
    if (value == null)
    {
      return 1;
    }
    else
    {
      return 1 + FfiConverterUInt32.INSTANCE.AllocationSize((uint)value);
    }
  }

  public override void Write(uint? value, BigEndianStream stream)
  {
    if (value == null)
    {
      stream.WriteByte(0);
    }
    else
    {
      stream.WriteByte(1);
      FfiConverterUInt32.INSTANCE.Write((uint)value, stream);
    }
  }
}




class FfiConverterOptionalString : FfiConverterRustBuffer<String?>
{
  public static FfiConverterOptionalString INSTANCE = new FfiConverterOptionalString();

  public override String? Read(BigEndianStream stream)
  {
    if (stream.ReadByte() == 0)
    {
      return null;
    }
    return FfiConverterString.INSTANCE.Read(stream);
  }

  public override int AllocationSize(String? value)
  {
    if (value == null)
    {
      return 1;
    }
    else
    {
      return 1 + FfiConverterString.INSTANCE.AllocationSize((String)value);
    }
  }

  public override void Write(String? value, BigEndianStream stream)
  {
    if (value == null)
    {
      stream.WriteByte(0);
    }
    else
    {
      stream.WriteByte(1);
      FfiConverterString.INSTANCE.Write((String)value, stream);
    }
  }
}




class FfiConverterSequenceTypeIotzioInfo : FfiConverterRustBuffer<List<IotzioInfo>>
{
  public static FfiConverterSequenceTypeIotzioInfo INSTANCE = new FfiConverterSequenceTypeIotzioInfo();

  public override List<IotzioInfo> Read(BigEndianStream stream)
  {
    var length = stream.ReadInt();
    var result = new List<IotzioInfo>(length);
    for (int i = 0; i < length; i++)
    {
      result.Add(FfiConverterTypeIotzioInfo.INSTANCE.Read(stream));
    }
    return result;
  }

  public override int AllocationSize(List<IotzioInfo> value)
  {
    var sizeForLength = 4;

    // details/1-empty-list-as-default-method-parameter.md
    if (value == null)
    {
      return sizeForLength;
    }

    var sizeForItems = value.Select(item => FfiConverterTypeIotzioInfo.INSTANCE.AllocationSize(item)).Sum();
    return sizeForLength + sizeForItems;
  }

  public override void Write(List<IotzioInfo> value, BigEndianStream stream)
  {
    // details/1-empty-list-as-default-method-parameter.md
    if (value == null)
    {
      stream.WriteInt(0);
      return;
    }

    stream.WriteInt(value.Count);
    value.ForEach(item => FfiConverterTypeIotzioInfo.INSTANCE.Write(item, stream));
  }
}
#pragma warning restore 8625
public static class IotzioMethods
{
}


public static class AndroidHelper
{

  private static bool libraryLoaded = false;

  /// <summary>
  ///
  /// Call this always on your MainActivity OnCreate function as stated:
  ///
  /// AndroidHelper.OnActivityCreate(Java.Interop.EnvironmentPointer, this.Handle);
  ///
  /// </summary>
  /// <param name="javaEnvironmentPointer">Java.Interop.EnvironmentPointer</param>
  /// <param name="androidContextHandle">Handle of any java class that is instance of Android.Content.Context</param>
  /// <exception cref="Exception"></exception>
  public static void OnActivityCreate(IntPtr javaEnvironmentPointer, IntPtr androidContextHandle)
  {
    if (OperatingSystem.IsAndroid() && !libraryLoaded)
    {
      var result = OnActivityCreateNative(javaEnvironmentPointer, IntPtr.Zero, androidContextHandle);

      if (result != 1)
      {
        throw new Exception("Failed to initialize Android Context.");
      }

      libraryLoaded = true;
    }
  }

  [DllImport("iotzio_core", EntryPoint = "Java_com_iotzio_api_AndroidHelper_onActivityCreateNative")]
  private static extern byte OnActivityCreateNative(IntPtr env, IntPtr thiz, IntPtr context);
}

