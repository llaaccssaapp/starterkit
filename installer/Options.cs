using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using CommandLine;
using Installer.Helper;
using Microsoft.Extensions.Logging;

namespace Installer
{
    [Singleton]
    public class Options
    {
        [Option('v', "verbose", Required = false, HelpText = "Set output to verbose messages.")]
        public bool Verbose { get; set; }

        [Option('d', "dns", Required = false, HelpText = "Set DNS Name to be used.", Default = "starterkit.devops.family")]
        public string Dns { get; set; }
        
        internal void LogOptions(ILogger logger)
        {
            logger.LogInformation("======================================================================");
            logger.LogInformation("Command Line Options");
            logger.LogInformation("======================================================================");
            logger.LogInformation($"Verbose:           {this.Verbose}");
            logger.LogInformation($"Dns:               {this.Dns}");
            logger.LogInformation($"CurrentDirectory:  {Directory.GetCurrentDirectory()}");
        }
        
        internal static Options CreateOptions(string[] args)
        {
            var options = new Options();
            Parser.Default.ParseArguments<Options>(args)
                .WithParsed(o => 
                {
                    options = o;
                })
                .WithNotParsed(errorList => 
                {
                    errorList.ToList().ForEach(error => 
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"Error {error.Tag}");
                        Console.ForegroundColor = ConsoleColor.White;
                    });
                });
            
            return options;
        }

        public void Ask(string query, string defaultAnswer = "n", Action yesAction = null, Action noAction = null)
        {
            Console.Write($"{query} ({defaultAnswer}):");
            var answer = Console.ReadLine();
            if (answer.Equals("y") || (defaultAnswer == "y" && string.IsNullOrEmpty(answer)))
            {
                yesAction?.Invoke();
            }
            else
            {
                noAction?.Invoke();
            }
        }
    }
}