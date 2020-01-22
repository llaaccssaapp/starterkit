﻿using System;
using System.Collections.Generic;
using System.Net;
using static Bullseye.Targets;
using static SimpleExec.Command;
using static System.Console;
using RestSharp;
using System.Linq;
using System.IO;
using CommandLine;
using installer;
using System.Net.NetworkInformation;
namespace Installer
{
    static class Program
    {
        private static string rancherInternalUrl;
        private static string rancherUrl;

        static void Main(string[] args)
        {
            ServicePointManager.ServerCertificateValidationCallback += (sender, certificate, chain, sslPolicyErrors) => true;
            var options = SetOptions(args);
            DefineTargets(options);
            rancherUrl = "starterkit.devops.family";
            rancherInternalUrl = GetRancherInternalUrl(options);
            var targets = DefineTargetsToBeExecuted(options);
            if(targets != null)
            {
                RunTargetsAndExit(targets);
            }
        }
        private static Options SetOptions(string[] args)
        {
            Options options = new Options();
            Parser.Default.ParseArguments<Options>(args)
                .WithParsed(o => 
                {
                    WriteHeader("Command Line Options");
                    System.Console.WriteLine($"Dns:               {o.Dns}");
                    System.Console.WriteLine($"Verbose:           {o.Verbose}");
                    System.Console.WriteLine($"InstallRancher:    {o.InstallRancher}");
                    System.Console.WriteLine($"InstallStarterKit: {o.InstallStarterKit}");
                    System.Console.WriteLine($"SslHack:           {o.SslHack}");
                    System.Console.WriteLine($"DynamicDns:        {o.DynamicDns}");
                    System.Console.WriteLine($"Tasks overwrite:   {String.Join(' ', o.Tasks)}");
                    options = o;
                })
                .WithNotParsed(errorList => 
                {
                    errorList.ToList().ForEach(error => 
                    {
                        System.Console.WriteLine($"Error {error.Tag}");
                    });
                });
            return options;
        }
        private static IEnumerable<string> DefineTargetsToBeExecuted(Options options)
        {
            if(options.Tasks == null || options.Tasks.Any() == true)
            {
                return options.Tasks;
            }
            
            var targets = new List<string>();
            if(options.SslHack)
            {
                targets.Add("installCertificates");
            }
            
            targets.Add("checkKubeclt");
            
            if(options.InstallRancher == true)
            {
                targets.Add("installIngress");
                targets.Add("installRancher");
                targets.Add("configureRancher");
            }
            
            if(options.InstallStarterKit)
            {
                targets.Add("installStarterKit");
            }
            
            targets.AddRange(options.Tasks);
            return targets;
        }
        
        private static void DefineTargets(Options options)
        {
            var ssl = "self";
            Target(
                "default", 
                DependsOn("checkKubeclt", "installIngress", "installRancher", "configureRancher", "installStarterKit"), 
                () => { WriteLine($"default "); });

            Target("installCertificates",  () =>
            {
                WriteHeader("Installing root CAs of helm repos");
                var urls = new List<string>();
                urls.Add("kubernetes-charts.storage.googleapis.com");
                urls.Add("charts.jetstack.io");
                urls.Add("releases.rancher.com");
                foreach (var url in urls)
                {
                    File.WriteAllText("install.sh",
                        $"openssl s_client -showcerts -verify 5 -connect " + url +
                        ":443 < /dev/null | awk '/BEGIN/,/END/{ if(/BEGIN/){a++}; out=\"cert\"a\".crt\"; print >out}' && for cert in *.crt; do newname=$(openssl x509 -noout -subject -in $cert | sed -n 's/^.*CN=\\(.*\\)$/\\1/; s/[ ,.*]/_/g; s/__/_/g; s/^_//g;p').crt; mv $cert /usr/local/share/ca-certificates/$newname; done && update-ca-certificates");
                    Run("bash", "install.sh");
                }
            });
            
            Target("checkKubeclt", () =>
            {
                try
                {
                    Run("kubectl", "get nodes");
                    try
                    {
                        var hostAddress = Dns.GetHostEntry("host.docker.internal").AddressList.First().ToString();
                        WriteLine("Running inside Docker");
                        WriteLine($"Add {hostAddress} {rancherInternalUrl} to host");
                        File.AppendAllText("/etc/hosts", $"{hostAddress} {rancherInternalUrl}{Environment.NewLine}");
                    }
                    catch
                    {
                        WriteLine("Running outside Docker");
                    }
                }
                catch
                {
                    WriteLine("Kube folder is not mapped. Please start this container with:");
                    WriteLine("-v $HOME/.kube:/root/.kube");
                    throw;
                }
            });
            
            Target("installIngress", DependsOn("checkKubeclt"), () =>
            {
                WriteHeader("Install Ingress");
                Kubernetes.InstallResourceIfNotExists("ingress-nginx", "namespace");
                Kubernetes.InstallResourceIfNotExists(
                    "ingress-default-cert",
                    "secret tls",
                    "ingress-nginx",
                    $"--cert=./../ssl/{ssl}/server.crt --key=./../ssl/{ssl}/server.key ");
                Kubernetes.InstallApplicationeIfNotExists(
                    "ingress-nginx",
                    "stable/nginx-ingress",
                    "ingress-nginx",
                    "--set controller.extraArgs.default-ssl-certificate=ingress-nginx/ingress-default-cert",
                    "deployment.apps/ingress-nginx-nginx-ingress-controller",
                    "deployment.apps/ingress-nginx-nginx-ingress-default-backend");
            });
            
            Target("installRancher", DependsOn("checkKubeclt"), () =>
            {
                WriteHeader("Install Rancher");
                Kubernetes.InstallResourceIfNotExists("cattle-system", "namespace");
                Kubernetes.InstallResourceIfNotExists(
                    "tls-rancher-ingress",
                    "secret tls",
                    "cattle-system",
                    $"--cert=./../ssl/{ssl}/server.crt --key=./../ssl/{ssl}/server.key ");
                Kubernetes.InstallResourceIfNotExists(
                    "tls-ca",
                    "secret generic",
                    "cattle-system",
                    $"--from-file=./../ssl/{ssl}/cacerts.pem");
                Kubernetes.InstallApplicationeIfNotExists(
                    "rancher",
                    "rancher-latest/rancher",
                    "cattle-system",
                    $"--set hostname={rancherInternalUrl} " +
                    "--set privateCA=true " +
                    "--set ingress.tls.source=secret",
                    "deploy/rancher");
                Run("kubectl", "apply -f ./../components/rancher/rancher.ingress.yml");
            });
            
            Target("configureRancher", DependsOn("checkKubeclt"), () =>
            {
                WriteHeader("Configure Rancher");
                var client = new RestClient($"https://{rancherInternalUrl}");
                Rancher.WaitUntilIsUpAndReady(client);
                var token = Rancher.Login(client);
                Rancher.ChangePassword(client, token);
                Rancher.SetServerUrl(client, rancherInternalUrl);
            });
            
            Target("installStarterKit", DependsOn("checkKubeclt"), () =>
            {
                WriteHeader("Install Starterkit");
                Kubernetes.InstallResourceIfNotExists("starterkit", "namespace");
                Run("kubectl", "apply -f ./../components/common/cert.yaml");
                Kubernetes.InstallApplicationeIfNotExists(
                    "ldap",
                    "stable/openldap",
                    "starterkit",
                    $"-f ./../components/openldap/values.yaml",
                    "deploy/ldap-openldap");
                Kubernetes.InstallApplicationeIfNotExists(
                    "ldap-ui",
                    "",
                    "starterkit",
                    $"./../components/openldapui",
                    "deploy/ldap-ui-openldapui");
                Kubernetes.InstallResourceIfNotExists(
                    "starterkit",
                    "configmap",
                    "starterkit",
                    "--from-file=./../components/jenkins/config");
                Kubernetes.InstallApplicationeIfNotExists(
                    "jenkins",
                    "stable/jenkins",
                    "starterkit",
                    $"-f ./../components/jenkins/values.yaml --set master.ingress.hostName=jenkins.{rancherUrl}",
                    "deploy/jenkins");
            });
        }
        private static string GetRancherInternalUrl(Options options)
        {
            // if(! string.IsNullOrEmpty(options.Dns))
            // {
            //     System.Console.WriteLine($"Using {options.Dns} as base url");
            //     return options.Dns;
            // }
            // else
            // {
                var list = new List<string>();
                list.Add("192.168.65.1");
                // list.Add("10.0.75.1");
                foreach (var ip in list)
                {
                    System.Console.WriteLine($"Try to ping {ip}");
                    var ping = new Ping();
                    var result = ping.Send(ip);
                    if(result.Status == IPStatus.Success)
                    {
                        var url = $"starterkit.{result.Address}.{options.DynamicDns}";
                        System.Console.WriteLine($"Using {url} as base url");
                        return url;
                    }
                    
                }
            // }
                
            throw new Exception("Script was not able to find a ip to be used. Please set it manually with the --dns flag");
        }
        
        public static void WriteHeader(string name)
        {
            WriteLine("####################################");
            WriteLine(name);
            WriteLine("####################################");
        }
    }
}