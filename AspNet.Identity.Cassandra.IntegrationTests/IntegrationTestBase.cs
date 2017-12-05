﻿using System;
using Cassandra;
using Microsoft.AspNet.Identity;
using NUnit.Framework;

namespace AspNet.Identity.Cassandra.IntegrationTests
{
    /// <summary>
    /// Base class for integration test fixtures.  Does some common setup/teardown.
    /// </summary>
    public abstract class IntegrationTestBase
    {
        private const string TestKeyspaceFormat = "aspnet_identity_{0}";

        protected RoleManager<Role, Guid> RoleManager;
        protected UserManager<User, Guid> UserManager;
        private UserStore _userStore;
        private RoleStore _roleStore;
        private ISession _session;
        private string _keyspaceName;

        [TestFixtureSetUp]
        public virtual void TestSetup()
        {
            var cluster = Cluster.Builder()
                .AddContactPoint("127.0.0.1")
                .Build();
            _session = cluster.Connect();

            // Use a unique keyspace for each test fixture named after the test fixture's class name
            _keyspaceName = string.Format(TestKeyspaceFormat, GetType().Name.Replace(".", string.Empty));

            // Drop and re-create the keyspace
            _session.DeleteKeyspaceIfExists(_keyspaceName);
            _session.CreateKeyspaceIfNotExists(_keyspaceName);
            _session.ChangeKeyspace(_keyspaceName);

            _userStore = new UserStore(_session);

            // Exercise the UserManager class in tests since that's how consumers will use CassandraUserStore
            UserManager = new UserManager<User, Guid>(_userStore);

            _roleStore = new RoleStore(_session);

            // Exercise the RoleManager class in tests since that's how consumers will use CassandraRoleStore
            RoleManager = new RoleManager<Role, Guid>(_roleStore);
        }

        [TestFixtureTearDown]
        public virtual void TestTearDown()
        {
            _session.DeleteKeyspaceIfExists(_keyspaceName);

            UserManager.Dispose();
            _userStore.Dispose();
            
            RoleManager.Dispose();
            _roleStore.Dispose();

            _session.Dispose();
        }
    }
}