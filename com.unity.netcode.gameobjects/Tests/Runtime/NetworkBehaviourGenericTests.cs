using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Unity.Netcode.TestHelpers.Runtime;

namespace Unity.Netcode.RuntimeTests
{
    /// <summary>
    /// This class is for testing general fixes or functionality of NetworkBehaviours
    /// </summary>
    public class NetworkBehaviourGenericTests : NetcodeIntegrationTest
    {
        protected override int NbClients => 0;

        protected override IEnumerator OnPreSetup()
        {
            // Make sure we don't automatically start
            m_BypassStartAndWaitForClients = true;
            return base.OnPreSetup();
        }

        public class SimpleNetworkBehaviour : NetworkBehaviour
        {
        }

        /// <summary>
        /// This test validates a fix to NetworkBehaviour.NetworkObject when
        /// the NetworkManager.LogLevel is set to Developer
        /// Note: This test does not require any clients, but should not impact this
        /// particular test if new tests are added to this class that do require clients
        /// </summary>
        [Test]
        public void ValidateNoSpam()
        {
            var objectToTest = new GameObject();
            var simpleNetworkBehaviour = objectToTest.AddComponent<SimpleNetworkBehaviour>();

            // Now just start the Host
            Assert.True(NetcodeIntegrationTestHelpers.Start(true, m_ServerNetworkManager, new NetworkManager[] { }), "Failed to start the host!");

            // set the log level to developer
            m_ServerNetworkManager.LogLevel = LogLevel.Developer;

            // Verify the warning gets logged under normal conditions
            var isNull = simpleNetworkBehaviour.NetworkObject == null;
            LogAssert.Expect(LogType.Warning, $"[Netcode] Could not get {nameof(NetworkObject)} for the {nameof(NetworkBehaviour)}. Are you missing a {nameof(NetworkObject)} component?");

            var networkObjectToTest = objectToTest.AddComponent<NetworkObject>();
            networkObjectToTest.NetworkManagerOwner = m_ServerNetworkManager;
            networkObjectToTest.Spawn();

            // Assure no log messages are logged when they should not be logged
            isNull = simpleNetworkBehaviour.NetworkObject != null;
            LogAssert.NoUnexpectedReceived();

            networkObjectToTest.Despawn();
            Object.Destroy(networkObjectToTest);
        }
    }
}
