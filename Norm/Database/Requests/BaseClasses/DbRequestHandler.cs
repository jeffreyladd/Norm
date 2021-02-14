﻿using MediatR;
using Norm.Database.Contexts;
using System.Threading;
using System.Threading.Tasks;

namespace Norm.Database.Requests.BaseClasses
{
    public abstract class DbRequestHandler<T, U> : IRequestHandler<T, DbResult<U>> where T : IRequest<DbResult<U>>
    {
        public DbRequestHandler(IDbContext db)
        {
            this.DbContext = db;
        }
        public abstract Task<DbResult<U>> Handle(T request, CancellationToken cancellationToken);

        protected IDbContext DbContext { get; }
    }

    public abstract class DbRequestHandler<T> : IRequestHandler<T, DbResult> where T : IRequest<DbResult>
    {
        public DbRequestHandler(IDbContext dbContext)
        {
            this.DbContext = dbContext;
        }
        public abstract Task<DbResult> Handle(T request, CancellationToken cancellationToken);

        protected IDbContext DbContext { get; }
    }
}
