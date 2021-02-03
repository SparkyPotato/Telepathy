﻿using System;
using System.Net.Sockets;
using System.Threading;

namespace Telepathy
{
    public class Client : Common
    {
        // events to hook into
        // => OnData uses ArraySegment for allocation free receives later
        public Action OnConnected;
        public Action<ArraySegment<byte>> OnData;
        public Action OnDisconnected;

        public TcpClient client;
        Thread receiveThread;
        Thread sendThread;

        // TcpClient.Connected doesn't check if socket != null, which
        // results in NullReferenceExceptions if connection was closed.
        // -> let's check it manually instead
        public bool Connected => client != null &&
                                 client.Client != null &&
                                 client.Client.Connected;

        // TcpClient has no 'connecting' state to check. We need to keep track
        // of it manually.
        // -> checking 'thread.IsAlive && !Connected' is not enough because the
        //    thread is alive and connected is false for a short moment after
        //    disconnecting, so this would cause race conditions.
        // -> we use a threadsafe bool wrapper so that ThreadFunction can remain
        //    static (it needs a common lock)
        // => Connecting is true from first Connect() call in here, through the
        //    thread start, until TcpClient.Connect() returns. Simple and clear.
        // => bools are atomic according to
        //    https://docs.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/variables
        //    made volatile so the compiler does not reorder access to it
        volatile bool _Connecting;
        public bool Connecting => _Connecting;

        // thread safe pipe for received messages
        readonly MagnificentReceivePipe receivePipe;

        // thread safe pipe to send messages from main thread to send thread
        readonly MagnificentSendPipe sendPipe;

        // ManualResetEvent to wake up the send thread. better than Thread.Sleep
        // -> call Set() if everything was sent
        // -> call Reset() if there is something to send again
        // -> call WaitOne() to block until Reset was called
        ManualResetEvent sendPending = new ManualResetEvent(false);

        // constructor
        public Client(int MaxMessageSize) : base(MaxMessageSize)
        {
            // create pipes with max message size for pooling
            receivePipe = new MagnificentReceivePipe(MaxMessageSize);
            sendPipe = new MagnificentSendPipe(MaxMessageSize);
        }

        // the thread function
        void ReceiveThreadFunction(string ip, int port)
        {
            // absolutely must wrap with try/catch, otherwise thread
            // exceptions are silent
            try
            {
                // connect (blocking)
                client.Connect(ip, port);
                _Connecting = false;

                // set socket options after the socket was created in Connect()
                // (not after the constructor because we clear the socket there)
                client.NoDelay = NoDelay;
                client.SendTimeout = SendTimeout;

                // start send thread only after connected
                // IMPORTANT: DO NOT SHARE STATE ACROSS MULTIPLE THREADS!
                sendThread = new Thread(() => { ThreadFunctions.SendLoop(0, client, sendPipe, sendPending); });
                sendThread.IsBackground = true;
                sendThread.Start();

                // run the receive loop
                // IMPORTANT: DO NOT SHARE STATE ACROSS MULTIPLE THREADS!
                ThreadFunctions.ReceiveLoop(0, client, MaxMessageSize, receivePipe, QueueLimit);
            }
            catch (SocketException exception)
            {
                // this happens if (for example) the ip address is correct
                // but there is no server running on that ip/port
                Log.Info("Client Recv: failed to connect to ip=" + ip + " port=" + port + " reason=" + exception);

                // add 'Disconnected' event to receive pipe so that the caller
                // knows that the Connect failed. otherwise they will never know
                receivePipe.Enqueue(EventType.Disconnected, default);
            }
            catch (ThreadInterruptedException)
            {
                // expected if Disconnect() aborts it
            }
            catch (ThreadAbortException)
            {
                // expected if Disconnect() aborts it
            }
            catch (Exception exception)
            {
                // something went wrong. probably important.
                Log.Error("Client Recv Exception: " + exception);
            }

            // sendthread might be waiting on ManualResetEvent,
            // so let's make sure to end it if the connection
            // closed.
            // otherwise the send thread would only end if it's
            // actually sending data while the connection is
            // closed.
            sendThread?.Interrupt();

            // Connect might have failed. thread might have been closed.
            // let's reset connecting state no matter what.
            _Connecting = false;

            // if we got here then we are done. ReceiveLoop cleans up already,
            // but we may never get there if connect fails. so let's clean up
            // here too.
            client?.Close();
        }

        public void Connect(string ip, int port)
        {
            // not if already started
            if (Connecting || Connected)
            {
                Log.Warning("Telepathy Client can not create connection because an existing connection is connecting or connected");
                return;
            }

            // We are connecting from now until Connect succeeds or fails
            _Connecting = true;

            // create a TcpClient with perfect IPv4, IPv6 and hostname resolving
            // support.
            //
            // * TcpClient(hostname, port): works but would connect (and block)
            //   already
            // * TcpClient(AddressFamily.InterNetworkV6): takes Ipv4 and IPv6
            //   addresses but only connects to IPv6 servers (e.g. Telepathy).
            //   does NOT connect to IPv4 servers (e.g. Mirror Booster), even
            //   with DualMode enabled.
            // * TcpClient(): creates IPv4 socket internally, which would force
            //   Connect() to only use IPv4 sockets.
            //
            // => the trick is to clear the internal IPv4 socket so that Connect
            //    resolves the hostname and creates either an IPv4 or an IPv6
            //    socket as needed (see TcpClient source)
            client = new TcpClient(); // creates IPv4 socket
            client.Client = null; // clear internal IPv4 socket until Connect()

            // clear old messages in pipe, just to be sure that the caller
            // doesn't receive data from last time and gets out of sync.
            // -> calling this in Disconnect isn't smart because the caller may
            //    still want to process all the latest messages afterwards
            receivePipe.Clear();
            sendPipe.Clear();

            // client.Connect(ip, port) is blocking. let's call it in the thread
            // and return immediately.
            // -> this way the application doesn't hang for 30s if connect takes
            //    too long, which is especially good in games
            // -> this way we don't async client.BeginConnect, which seems to
            //    fail sometimes if we connect too many clients too fast
            receiveThread = new Thread(() => { ReceiveThreadFunction(ip, port); });
            receiveThread.IsBackground = true;
            receiveThread.Start();
        }

        public void Disconnect()
        {
            // only if started
            if (Connecting || Connected)
            {
                // close client
                client.Close();

                // wait until thread finished. this is the only way to guarantee
                // that we can call Connect() again immediately after Disconnect
                // -> calling .Join would sometimes wait forever, e.g. when
                //    calling Disconnect while trying to connect to a dead end
                receiveThread?.Interrupt();

                // we interrupted the receive Thread, so we can't guarantee that
                // connecting was reset. let's do it manually.
                _Connecting = false;

                // clear send pipe. no need to hold on to elements.
                // (unlike receiveQueue, which is still needed to process the
                //  latest Disconnected message, etc.)
                sendPipe.Clear();

                // let go of this one completely. the thread ended, no one uses
                // it anymore and this way Connected is false again immediately.
                client = null;
            }
        }

        // send message to server using socket connection.
        // arraysegment for allocation free sends later.
        // -> the segment's array is only used until Send() returns!
        public bool Send(ArraySegment<byte> message)
        {
            if (Connected)
            {
                // respect max message size to avoid allocation attacks.
                if (message.Count <= MaxMessageSize)
                {
                    // add to thread safe send pipe and return immediately.
                    // calling Send here would be blocking (sometimes for long
                    // times if other side lags or wire was disconnected)
                    sendPipe.Enqueue(message);
                    sendPending.Set(); // interrupt SendThread WaitOne()
                    return true;
                }
                Log.Error("Client.Send: message too big: " + message.Count + ". Limit: " + MaxMessageSize);
                return false;
            }
            Log.Warning("Client.Send: not connected!");
            return false;
        }

        // tick: processes up to 'limit' messages
        // => limit parameter to avoid deadlocks / too long freezes if server or
        //    client is too slow to process network load
        // => Mirror & DOTSNET need to have a process limit anyway.
        //    might as well do it here and make life easier.
        // => returns amount of remaining messages to process, so the caller
        //    can call tick again as many times as needed (or up to a limit)
        public int Tick(int processLimit)
        {
            // process up to 'processLimit' messages
            for (int i = 0; i < processLimit; ++i)
            {
                // peek first. allows us to process the first queued entry while
                // still keeping the pooled byte[] alive by not removing anything.
                if (receivePipe.TryPeek(out EventType eventType, out ArraySegment<byte> message))
                {
                    switch (eventType)
                    {
                        case EventType.Connected:
                            OnConnected?.Invoke();
                            break;
                        case EventType.Data:
                            OnData?.Invoke(message);
                            break;
                        case EventType.Disconnected:
                            OnDisconnected?.Invoke();
                            break;
                    }

                    // IMPORTANT: now dequeue and return it to pool AFTER we are
                    //            done processing the event.
                    receivePipe.TryDequeue();
                }
                // no more messages. stop the loop.
                else break;
            }

            // return what's left to process for next time
            return receivePipe.Count;
        }
    }
}
