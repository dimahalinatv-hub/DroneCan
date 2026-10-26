using System.IO.Ports;

namespace HobbywingCan.Core.Link;

/// <summary>Opens the flight controller's serial / USB-CDC port.</summary>
public sealed class SerialConnection : IDisposable
{
    private readonly SerialPort _port;

    private SerialConnection(SerialPort port) => _port = port;

    public Stream Stream => _port.BaseStream;

    public string PortName => _port.PortName;

    public static string[] GetPortNames() => SerialPort.GetPortNames().Distinct().OrderBy(p => p).ToArray();

    public static SerialConnection Open(string portName, int baudRate)
    {
        var port = new SerialPort(portName, baudRate)
        {
            // ArduPilot USB CDC only starts talking once DTR is asserted
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 1000,
        };
        port.Open();
        return new SerialConnection(port);
    }

    public void Dispose()
    {
        try
        {
            _port.Close();
        }
        catch (IOException)
        {
            // port vanished (USB unplugged)
        }
        _port.Dispose();
    }
}
