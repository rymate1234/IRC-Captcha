using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IrcClientCore;
using Microsoft.Extensions.Configuration;

namespace IrcCaptcha
{
    class Program
    {
        private static IrcSocket _socket;
        private static IConfigurationRoot config;

        static void Main(string[] args)
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

            config = builder.Build();
            
            var server = new IrcServer()
            {
                Name = "Server",
                Hostname = config.GetConnectionString("Hostname"),
                Port = Convert.ToInt32(config.GetConnectionString("Port")),
                Ssl = config.GetConnectionString("SSL") == "True",
                IgnoreCertErrors = true,
                Username = config.GetConnectionString("Username"),
                Channels = config.GetConnectionString("Channels")
            };
            
            _socket = new IrcSocket(server);
            _socket.Initialise();
            
            _socket.Connect();

            _socket.ChannelList.CollectionChanged += (s, e) => 
            {
                new ChannelHandler(config, _socket, (e.NewItems[0] as Channel)?.Name).Init();
            };
            var mre = new ManualResetEvent(false);

            _socket.HandleDisconnect += irc => { mre.Set(); };

            // The main thread can just wait on the wait handle, which basically puts it into a "sleep" state, and blocks it forever
            mre.WaitOne();
        }
    }
    
    class ChannelHandler
    {
        private readonly IrcSocket _socket;
        private readonly string _name;
        private readonly IConfigurationRoot _config;
        private readonly Random _random;
        private Dictionary<string, int> tracked = new Dictionary<string, int>();

        public ChannelHandler(IConfigurationRoot config, IrcSocket socket, string name)
        {
            _config = config;
            _socket = socket;
            _name = name;
            _random = new Random();
        }

        public void Init()
        {
            _socket.ChannelList[_name].Store.Users.CollectionChanged += UsersOnCollectionChanged;
            ((ObservableCollection<Message>) _socket.ChannelList[_name].Buffers).CollectionChanged += OnMessage;
        }

        private void SendMessage(string message)
        {
            _socket.CommandManager.HandleCommand(_name, message);
        }

        private void OnMessage(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action != NotifyCollectionChangedAction.Add) return;
            if (tracked.Count == 0) return;
            
            var message = e.NewItems[0] as Message;
            if (message == null || !tracked.ContainsKey(message.User)) return;
            if (!message.Text.Contains(tracked[message.User].ToString())) return;
            
            SendMessage($"Verified {message.User}!");
            tracked.Remove(message.User);
        }

        private void UsersOnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action != NotifyCollectionChangedAction.Add) return;
            
            var user = e.NewItems[0] as User;
            
            if 
            (
                user == null || 
                _config.GetSection("Settings")["WhitelistedNicks"].Contains(user.Nick) || 
                tracked.ContainsKey(user.Nick)
            ) return;
            
            var timeout = Convert.ToInt32(_config.GetSection("Settings")["Timeout"]);

            var first = _random.Next(1, 20);
            var second = _random.Next(1, 20);
            
            SendMessage($"Welcome {user.Nick}!");
            SendMessage($"Please verify your identity in {timeout} seconds or be purged!");
            SendMessage($"To do so, reply at any point with the answer to {first} + {second}");
            
            tracked.Add(user.Nick, first + second);
            
            Task.Factory.StartNew(() =>
            {
                Thread.Sleep(timeout * 1000);
                if (!tracked.ContainsKey(user.Nick)) return;
                
                SendMessage("Test failed!");
                SendMessage($"/kick {user.Nick}");
                SendMessage($"/mode {_name} +i");
                Thread.Sleep(60 * 1000);
                SendMessage($"/mode {_name} -i");
                tracked.Remove(user.Nick);
            });
        }
    }
}
