﻿using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OmniCore.Model.Interfaces.Data.Entities;

namespace OmniCore.Model.Interfaces.Data.Repositories
{
    public interface IRepository<T> : IRepositoryInitialization where T : IEntity
    {
        T New();
        Task Create(T entity, CancellationToken cancellationToken);
        Task<T> Read(long id, CancellationToken cancellationToken);
        IAsyncEnumerable<T> All(CancellationToken cancellationToken);
        Task Update(T entity, CancellationToken cancellationToken);
        Task Delete(T entity, CancellationToken cancellationToken);
    }
}
