using Bridge.Contract;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.ComponentModel.Composition.Primitives;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;

namespace Bridge.Translator
{
    public class Plugins : IPlugins
    {
        public static bool IsLoaded { get; set; }

        public static string GetPluginPath(ITranslator translator, IAssemblyInfo config)
        {
            string path = null;

            if (!string.IsNullOrWhiteSpace(config.PluginsPath))
            {
                path = Path.Combine(translator.FolderMode ? translator.Location : Path.GetDirectoryName(translator.Location), config.PluginsPath);
            }
            else
            {
                path = Path.Combine(translator.FolderMode ? translator.Location : Path.GetDirectoryName(translator.Location), "Bridge" + Path.DirectorySeparatorChar + "plugins");
            }

            return path;
        }

        protected class AssemblyResolver
        {
            public ILogger Logger { get; set; }

            public Assembly CurrentDomain_AssemblyResolve(object sender, System.ResolveEventArgs args)
            {
                var domain = sender as System.AppDomain;
                this.Logger.Trace("Domain " + domain.FriendlyName + " resolving assembly " + args.Name + " requested by " + args.RequestingAssembly.FullName + " ...");

                AssemblyName askedAssembly = new AssemblyName(args.Name);
                var assemblyLoaded = AssemblyResolver.CheckIfAssemblyLoaded(askedAssembly.Name, domain);

                if (assemblyLoaded != null)
                {
                    this.Logger.Trace("Resolved for " + assemblyLoaded.FullName);
                    return assemblyLoaded;
                }

                return null;
            }

            public static Assembly CheckIfAssemblyLoaded(string fullAssemblyName, System.AppDomain domain)
            {
                var assemblies = domain.GetAssemblies();
                foreach (var assembly in assemblies)
                {
                    var assemblyName = new AssemblyName(assembly.FullName);
                    if (assemblyName.Name == fullAssemblyName)
                    {
                        return assembly;
                    }
                }

                return null;
            }
        }

        public static IPlugins GetPlugins(ITranslator translator, IAssemblyInfo config, ILogger logger)
        {
            logger.Info("Discovering plugins...");

            if (!IsLoaded)
            {
                var resolver = new AssemblyResolver() { Logger = logger };

                System.AppDomain.CurrentDomain.AssemblyResolve += resolver.CurrentDomain_AssemblyResolve;

                IsLoaded = true;

                logger.Trace("Set assembly resolver event");
            }


            var path = GetPluginPath(translator, config);
            logger.Info("Will use the following plugin path \"" + path + "\"");

            var catalogs = new List<ComposablePartCatalog>();

            if (System.IO.Directory.Exists(path))
            {
                catalogs.Add(new DirectoryCatalog(path, "*.dll"));
                logger.Info("The plugin path exists. Will use it as DirectoryCatalog");
            }
            else
            {
                logger.Info("The plugin path does not exist. Skipping searching test framework plugins in the plugin folder.");
            }

            string[] skipPluginAssemblies = null;
            var translatorInstance = translator as Translator;
            if (translatorInstance != null)
            {
                skipPluginAssemblies = translatorInstance.SkipPluginAssemblies;
            }

            logger.Trace("Will search all translator references to find resource(s) with names starting from \"Bridge.Plugins.\" ...");

            foreach (var reference in translator.References)
            {
                logger.Trace("Searching plugins in reference " + reference.FullName + " ...");

                if (skipPluginAssemblies != null && skipPluginAssemblies.FirstOrDefault(x => reference.Name.FullName.Contains(x)) != null)
                {
                    logger.Trace("Skipping the reference " + reference.Name.FullName + " as it is in skipPluginAssemblies");
                    continue;
                }

                var assemblies = reference.MainModule.Resources.Where(res => res.Name.StartsWith("Bridge.Plugins."));

                logger.Info("The reference contains " + assemblies.Count() + " resource(s) needed");

                if (assemblies.Any())
                {
                    foreach (var res_assembly in assemblies)
                    {
                        logger.Trace("Searching plugins in resource " + res_assembly.Name + " ...");

                        try
                        {
                            using (var resourcesStream = ((EmbeddedResource)res_assembly).GetResourceStream())
                            {
                                var ba = new byte[(int)resourcesStream.Length];
                                resourcesStream.Read(ba, 0, (int)resourcesStream.Length);

                                logger.Trace("Read the assembly resource stream of " + resourcesStream.Length + " bytes length");

                                var trimmedName = res_assembly.Name.Remove(res_assembly.Name.IndexOf("Bridge.Plugins."), "Bridge.Plugins.".Length);
                                trimmedName = trimmedName.Remove(trimmedName.LastIndexOf(".dll"), 4);

                                var assembly = CheckIfAssemblyLoaded(logger, ba, null, trimmedName);

                                catalogs.Add(new AssemblyCatalog(assembly));
                                logger.Trace("The assembly " + assembly.FullName + " addied to the catalogs");
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.Error("Exception occurred:");
                            logger.Error(ex.Message);
                        }

                    }
                }
            }

            if (catalogs.Count == 0)
            {
                logger.Info("No AssemblyCatalogs found");
                return new Plugins() { plugins = new IPlugin[0] };
            }

            var catalog = new AggregateCatalog(catalogs);

            CompositionContainer container = new CompositionContainer(catalog);
            var plugins = new Plugins();

            logger.Info("ComposingParts to discover plugins...");

            try
            {
                container.ComposeParts(plugins);
            }
            catch(Exception ex)
            {
                logger.Error("Exception occurred:");
                logger.Error(ex.Message);
            }

            if (plugins.Parts != null)
            {

                foreach (var plugin in plugins.Parts)
                {
                    plugin.Logger = translator.Log;
                }

                logger.Info("Discovered " + plugins.Parts.Count() + " plugin(s)");
            }

            return plugins;
        }

        public static Assembly CheckIfAssemblyLoaded(ILogger logger, byte[] ba, AssemblyName assemblyName, string trimmedName)
        {
            logger.Trace("Check if assembly " + trimmedName + " already loaded");
            Assembly assembly = AssemblyResolver.CheckIfAssemblyLoaded(trimmedName, System.AppDomain.CurrentDomain);
            if (assembly != null)
            {
                logger.Trace("The assembly " + trimmedName + " is already loaded. Addin it to the catalogs");
            }
            else
            {
                logger.Trace("Loading the assembly into domain " + System.AppDomain.CurrentDomain.FriendlyName + " ...");

                if (ba != null)
                {
                    assembly = Assembly.Load(ba);
                }
                else
                {
                    assembly = Assembly.Load(assemblyName);
                }

                logger.Trace("Assembly " + assembly.FullName + " is loaded into domain " + System.AppDomain.CurrentDomain.FriendlyName);
            }
            return assembly;
        }

        [ImportMany]
        private IEnumerable<IPlugin> plugins;

        public IEnumerable<IPlugin> Parts
        {
            get
            {
                return this.plugins;
            }
        }

        public void OnConfigRead(IAssemblyInfo config)
        {
            foreach (var plugin in this.Parts)
            {
                plugin.OnConfigRead(config);
            }
        }

        public IEnumerable<string> GetConstructorInjectors(IConstructorBlock constructorBlock)
        {
            IEnumerable<string> result = new List<string>();
            foreach (var plugin in this.Parts)
            {
                result = result.Concat(plugin.GetConstructorInjectors(constructorBlock));
            }

            return result;
        }

        public bool HasConstructorInjectors(IConstructorBlock constructorBlock)
        {
            foreach (var plugin in this.Parts)
            {
                if (plugin.HasConstructorInjectors(constructorBlock))
                {
                    return true;
                }
            }

            return false;
        }

        public void BeforeEmit(IEmitter emitter, ITranslator translator)
        {
            foreach (var plugin in this.Parts)
            {
                plugin.BeforeEmit(emitter, translator);
            }
        }

        public void AfterEmit(IEmitter emitter, ITranslator translator)
        {
            foreach (var plugin in this.Parts)
            {
                plugin.AfterEmit(emitter, translator);
            }
        }

        public void AfterOutput(ITranslator translator, string outputPath, bool nocore)
        {
            foreach (var plugin in this.Parts)
            {
                plugin.AfterOutput(translator, outputPath, nocore);
            }
        }

        public void BeforeTypesEmit(IEmitter emitter, IList<ITypeInfo> types)
        {
            foreach (var plugin in this.Parts)
            {
                plugin.BeforeTypesEmit(emitter, types);
            }
        }

        public void AfterTypesEmit(IEmitter emitter, IList<ITypeInfo> types)
        {
            foreach (var plugin in this.Parts)
            {
                plugin.AfterTypesEmit(emitter, types);
            }
        }

        public void BeforeTypeEmit(IEmitter emitter, ITypeInfo type)
        {
            foreach (var plugin in this.Parts)
            {
                plugin.BeforeTypeEmit(emitter, type);
            }
        }

        public void AfterTypeEmit(IEmitter emitter, ITypeInfo type)
        {
            foreach (var plugin in this.Parts)
            {
                plugin.AfterTypeEmit(emitter, type);
            }
        }
    }
}
