using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace ConsoleApp1
{
    public class Program
    {
        public static void Main(string[] args)
        {
            const string ntpServerHostName = "time.windows.com";

            try
            {
                var networkTimeUtc = GetNetworkTimeUtc(ntpServerHostName);
                Console.WriteLine($"Network Time UTC: {networkTimeUtc}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unhandled exception when trying to retrieve network time from {ntpServerHostName}:");
                Console.WriteLine(ex);
            }

            Console.WriteLine("Press any key to quit...");
            Console.ReadKey();
        }


        /// <summary>
        /// The well-known NTP epoch: https://en.wikipedia.org/wiki/Network_Time_Protocol#Timestamps
        /// </summary>
        private static readonly DateTime _ntpEpochUtc = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// For fractional seconds, the number of seconds per interval (about 232 picoseconds).
        /// </summary>
        private static readonly double _ntpSecondsPerInterval = 1.0 / 0x100000000L;

        /// <summary>
        /// Lest you think I'm an actual network programmer, I adapted this code from a Stack Overflow answer:
        /// http://stackoverflow.com/a/12150289/731
        /// </summary>
        /// <param name="ntpServerHostName"></param>
        /// <returns></returns>
        private static DateTime? GetNetworkTimeUtc(string ntpServerHostName)
        {
            // NTP message size: 16 bytes of the digest (RFC 2030: https://tools.ietf.org/html/rfc2030#section-4)
            var ntpData = new byte[48];

            // The first byte contains the Leap Indicator, Version Number, and Mode values:
            //   * LI: two-bit code. No meaning in client request. Set to 0.
            //   * VN: three-bit integer. Set to 3 to use Version 3 (IPv4 only).
            //   * Mode: three-bit integer. Set to 3 to indicate that this is a client request.
            //
            // In other words, the first byte will be: 0b_00_011_011
            ntpData[0] = 0x1B;

            // The rest of the bytes are only significant in server responses. byte[] are initialized with 0s for
            //   each array element, which is fine for our purposes.


            //
            // Attempt the request to the NTP server.
            //

            IPHostEntry hostEntry = null;

            try
            {
                hostEntry = Dns.GetHostEntryAsync(ntpServerHostName)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (SocketException sex) when (sex.SocketErrorCode == SocketError.HostNotFound)
            {
                WriteError($"host '{ntpServerHostName}' not found.");
                return null;
            }

            if (hostEntry.AddressList.Length == 0)
            {
                WriteError($"DNS lookup for host '{ntpServerHostName}' did not return a list of IP addresses.");
                return null;
            }

            // The UDP port number for NTP is 123. https://tools.ietf.org/html/rfc4330#section-4
            var ipEndPoint = new IPEndPoint(hostEntry.AddressList[0], 123);

            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    // Timeout after 3 seconds so that the program doesn't hang indefinitely.
                    socket.ReceiveTimeout = 3 * 1000;

                    socket.Connect(ipEndPoint);
                    socket.Send(ntpData);
                    socket.Receive(ntpData);
                }
            }
            catch (SocketException sex) when (sex.SocketErrorCode == SocketError.TimedOut)
            {
                WriteError($"NTP server at '{ipEndPoint}' did not respond before the request timed out.");
                return null;
            }


            // Offset into the server response data, corresponding to the Transmit Timestamp, which is the time at 
            //   which the reply departed the server, in 64-bit timestamp format. https://tools.ietf.org/html/rfc4330#section-4
            const int transmitTimestampOffset = 40;

            // Get the seconds part.
            uint seconds = BitConverter.ToUInt32(ntpData, transmitTimestampOffset);

            // Get the fractional second intervals part.
            uint fractionalSecondIntervals = BitConverter.ToUInt32(ntpData, transmitTimestampOffset + 4);

            // NTP bits are numbered in Big Endian fashion (see: https://tools.ietf.org/html/rfc4330#section-3). 
            //   Windows uses Little Endian, so we have to swap them.
            seconds = SwapEndianness(seconds);
            fractionalSecondIntervals = SwapEndianness(fractionalSecondIntervals);

            //
            // The fractional part of an NTP timestamp is the number of ~232 picosecond intervals that have elapsed
            //   since the beginning of the current second. It's a 32-bit unsigned field, meaning the counter
            //   starts at 0x00000000 and reaches 0xFFFFFFFF, after which a new second starts and the fractional 
            //   counter rolls over to 0x00000000 again.
            //
            // There are 4,294,967,296 (2^32) such intervals for every second. If you flip that over to get the number 
            //   of seconds per interval, you get this:
            //
            //   * 1 second / 4,294,967,296 intervals = 2.3283064365387E-10 seconds/interval
            //
            // Hey, what a coincidence - it's ~232 picoseconds!
            //
            // So if you multiply the number of intervals elapsed times the number of seconds per interval, you'll
            //   get the fractional number of seconds that have elapsed since the beginning of the current second.
            //   From this, you can use standard techniques to compute milliseconds, microseconds, etc.
            //

            // Convert the whole seconds to milliseconds. Note we need to make this an explicit ulong operation, or
            //   else we'll lose some digits.
            ulong totalMilliseconds = ((ulong)seconds * 1000);

            // Convert the fractional second intervals to seconds, and then to milliseconds. Same as above - this 
            //   needs to be an explicit ulong operation in order to maintain precision.
            totalMilliseconds += (ulong)(fractionalSecondIntervals * _ntpSecondsPerInterval) * 1000;

            // Add milliseconds to the epoch to get the network time.
            return _ntpEpochUtc.AddMilliseconds(totalMilliseconds);
        }

        // See: http://stackoverflow.com/a/3294698/731
        static uint SwapEndianness(uint x)
        {
            return (((x & 0x000000ff) << 24) +
                    ((x & 0x0000ff00) << 8) +
                    ((x & 0x00ff0000) >> 8) +
                    ((x & 0xff000000) >> 24));
        }

        private static void WriteError(string message)
        {
            Console.Error.WriteLine($"Unable to retrieve network time: {message}");
        }
    }
}
