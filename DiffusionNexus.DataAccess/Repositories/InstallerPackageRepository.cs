using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.Domain.Entities;


namespace DiffusionNexus.DataAccess.Repositories
{
    internal sealed class InstallerPackageRepository : RepositoryBase<InstallerPackage>, IInstallerPackageRepository
    {
        public InstallerPackageRepository(DiffusionNexusCoreDbContext context) : base(context)
        {
        }
    }
}
