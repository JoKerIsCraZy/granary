using Granary.Core;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Microsoft.Extensions.DependencyInjection;

namespace Granary.Tests.Catalog;

public sealed class CatalogServicesTests
{
    [Theory]
    [InlineData(typeof(CatalogApi))]
    [InlineData(typeof(ImagesApi))]
    public void AddGranaryCore_RegistersTheApiAsTransient(Type api)
    {
        var services = new ServiceCollection().AddGranaryCore();

        var descriptor = Assert.Single(services, d => d.ServiceType == api);
        Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
    }
}
