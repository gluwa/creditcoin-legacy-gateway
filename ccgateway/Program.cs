/*
    Copyright(c) 2018 Gluwa, Inc.

    This file is part of Creditcoin.

    Creditcoin is free software: you can redistribute it and/or modify
    it under the terms of the GNU Lesser General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.
    
    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
    GNU Lesser General Public License for more details.
    
    You should have received a copy of the GNU Lesser General Public License
    along with Creditcoin. If not, see <https://www.gnu.org/licenses/>.
*/

using ccplugin;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NetMQ;
using NetMQ.Sockets;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using CCGatewayPlugin;
using System.Threading;

namespace ccgateway
{
    class Program
    {
        static string folder;
        static IConfiguration config;
        public static bool verboseLogging = false;

        static async Task ProcessRequest(Loader<ICCGatewayPluginAsync> loader, NetMQFrame id, string requestString)
        {
            string[] command = requestString.Split();
            string response;

            if (command.Length < 2)
            {
                response = "poor";
                Console.WriteLine($"{getTimestamp()}: Not enough parameters ({requestString}).");
            }
            else
            {
                string action = command[0];
                if (verboseLogging) 
                    Console.WriteLine($"{getTimestamp()}: Processing action '{action}' ({requestString}).");

                ICCGatewayPluginAsync plugin = loader.Get(action);
                if (plugin == null)
                {
                    if (verboseLogging)
                        Console.WriteLine($"{getTimestamp()}: Plugin for {action} not found!");
                    response = "miss";
                }
                else
                {
                    command = command.Skip(1).ToArray();
                    var pluginConfig = config.GetSection(action);
                    if (verboseLogging)
                        Console.WriteLine($"{getTimestamp()}: Processing request '{requestString}'.");
                    Tuple<bool, string> result;
                    try
                    {
                        result = await plugin.Run(pluginConfig, command);
                    } catch (Exception ex)
                    {
                        result = Tuple.Create(false, ex.Message);
                    }
                    Debug.Assert(result.Item2 != null);
                    if (result.Item1)
                    {
                        if (verboseLogging)
                            Console.WriteLine($"{getTimestamp()}: Request '{requestString}' succeeded.");
                        response = "good";
                    }
                    else
                    {
                        StringBuilder err = new StringBuilder();
                        err.Append(requestString).Append(": ").Append(result.Item2);
                        Console.WriteLine($"{getTimestamp()}: {err.ToString()}");
                        response = "fail";
                    }
                }
            }

            using (var socket = new DealerSocket(">inproc://back"))
            {
                var message = new NetMQMessage();
                message.Append(id);
                message.AppendEmptyFrame();
                message.Append(response);
                socket.SendMultipartMessage(message);
            }
        }

        static void ServerAsync(string ip)
        {
            var loader = new Loader<ICCGatewayPluginAsync>();
            var msgs = new List<string>();

            loader.Load(folder, msgs);
            foreach (var msg in msgs)
            {
                Console.WriteLine(msg);
            }
            using (var frontEnd = new RouterSocket($"@tcp://{ip}:55555"))
            using (var backEnd = new DealerSocket("@inproc://back"))
            using (var poller = new NetMQPoller { frontEnd, backEnd })
            {
                frontEnd.ReceiveReady += (sender, args) =>
                {
                    try
                    {
                        var message = args.Socket.ReceiveMultipartMessage();
                        var identity = message.First;
                        var request = message[2].ConvertToString();

                        ThreadPool.QueueUserWorkItem(async ctx =>
                        {
                            var (loader, id, contents) = (Tuple<Loader<ICCGatewayPluginAsync>, NetMQFrame, string>)ctx;
                            await ProcessRequest(loader, id, contents);
                        }, Tuple.Create(loader, identity, request));
                    }
                    catch (System.ArgumentOutOfRangeException e)
                    {
                        Console.WriteLine($"{getTimestamp()}: Exception caught, won't crash: {e}");
                    }
                };
                backEnd.ReceiveReady += (sender, args) =>
                {
                    var message = args.Socket.ReceiveMultipartMessage();
                    frontEnd.SendMultipartMessage(message);
                };

                poller.Run();
            }
        }

        static string getTimestamp()
        {
            return DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffffffK");
        }

        static void Main(string[] args)
        {
            if (args.Length > 0)
            {
                if (args[0] == "-v")
                {
                    verboseLogging = true;
                    Console.WriteLine($"{getTimestamp()}: Argument '-v' found -- Verbose logging enabled.");
                }
            }

            config = new ConfigurationBuilder()
                    .AddJsonFile("appsettings.json", true, false)
                    .AddJsonFile("appsettings.dev.json", true, true)
                .Build();

            string root = Directory.GetCurrentDirectory();
            folder = TxBuilder.GetPluginsFolder(root);
            if (folder == null)
            {
                Console.WriteLine("Failed to locate plugin folder");
                return;
            }

            string ip = config["bindIP"];
            if (string.IsNullOrWhiteSpace(ip))
            {
                Console.WriteLine("bindIP is not set.. defaulting to 127.0.0.1 local connection only");
                ip = "127.0.0.1";
            }

            ServerAsync(ip);
        }
    }
}