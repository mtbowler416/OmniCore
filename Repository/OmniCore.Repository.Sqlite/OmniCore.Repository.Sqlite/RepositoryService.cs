﻿using Nito.AsyncEx;
using OmniCore.Model.Interfaces.Services;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Unity;

namespace OmniCore.Repository.Sqlite
{
    public class RepositoryService : IRepositoryService
    {
        public bool IsInitialized { get; private set; }

        private AsyncLock InitializeLock = new AsyncLock();
        public string RepositoryPath { get; private set; }
        public Task Restore(string backupPath)
        {
            throw new NotImplementedException();
        }

        public Task Backup(string backupPath)
        {
            throw new NotImplementedException();
        }

        public Task<bool> IsValid(string repositoryPath)
        {
            throw new NotImplementedException();
        }

        public async Task New(string repositoryPath)
        {
            using var initializeLock = await InitializeLock.LockAsync();
            RepositoryPath = repositoryPath;
            IsInitialized = true;
        }

        public async Task Initialize(string repositoryPath)
        {
            using var initializeLock = await InitializeLock.LockAsync();
            if (!IsInitialized)
            {
                RepositoryPath = repositoryPath;
                IsInitialized = true;
            }
        }

        public Task Shutdown()
        {
            throw new NotImplementedException();
        }
    }
}