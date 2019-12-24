﻿using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OmniCore.Model.Interfaces.Platform;
using OmniCore.Model.Interfaces.Services;
using Plugin.BluetoothLE;

namespace OmniCore.Client.Droid.Services
{
    public class CoreServices : ICoreServices
    {
        public ICoreApplicationServices CoreApplicationServices { get; }
        public ICoreDataServices CoreDataServices { get; }
        public ICoreIntegrationServices CoreIntegrationServices { get; }

        public CoreServices(
            ICoreApplicationServices coreApplicationServices,
            ICoreDataServices coreDataServices,
            ICoreIntegrationServices coreIntegrationServices)
        {
            CoreApplicationServices = coreApplicationServices;
            CoreDataServices = coreDataServices;
            CoreIntegrationServices = coreIntegrationServices;
        }

        public async Task StartUp()
        {

            var dbPath = Path.Combine(CoreApplicationServices.DataPath, "oc.db3");
            await CoreDataServices.RepositoryService.Initialize(dbPath, CancellationToken.None);
        }

        public async Task ShutDown()
        {
            await CoreDataServices.RepositoryService.Shutdown(CancellationToken.None);
        }

    }
}