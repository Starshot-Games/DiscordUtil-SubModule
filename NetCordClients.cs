using NetCord.Gateway;
using NetCord.Rest;

namespace JMTech.Shared.NetCord;

public sealed class NetCordClients(GatewayClient gateway, RestClient rest)
{
    public readonly GatewayClient gateway = gateway;
    public readonly RestClient rest = rest;
}