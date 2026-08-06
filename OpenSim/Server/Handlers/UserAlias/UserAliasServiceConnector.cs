using System;
using Nini.Config;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;

namespace OpenSim.Server.Handlers.UserAlias
{
    public class UserAliasServiceConnector : ServiceConnector
    {
        private IUserAliasService m_UserAliasService;
        private string m_ConfigName = "UserAliasService";

        public UserAliasServiceConnector(IConfigSource config, IHttpServer server, string configName) :
                base(config, server, configName)
        {
            IConfig serverConfig = config.Configs[m_ConfigName];
            if (serverConfig == null)
                throw new Exception(String.Format("No section {0} in config file", m_ConfigName));

            string service = serverConfig.GetString("LocalServiceModule", String.Empty);

            if (service == String.Empty)
                throw new Exception("LocalServiceModule not present in UserAliasService config file UserAliasService section");

            Object[] args = new Object[] { config };
            m_UserAliasService = ServerUtils.LoadPlugin<IUserAliasService>(service, args);

            IServiceAuth auth = ServiceAuth.Create(config, m_ConfigName);

            server.AddStreamHandler(new UserAliasServerPostHandler(m_UserAliasService, serverConfig, auth));
        }
    }
}
