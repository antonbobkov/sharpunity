﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.IO;
using System.Diagnostics;

using Tools;
using Network;


namespace ServerClient
{
    class Server : GameNode
    {
        public static readonly OverlayHostName hostName = new OverlayHostName("server");

        Random r = new Random();

        List<IPEndPoint> validatorPool = new List<IPEndPoint>();
        
        //List<Point> spawnWorlds = new List<Point>();
        Dictionary<Point, WorldInfo> worlds = new Dictionary<Point, WorldInfo>();
        Dictionary<Guid, PlayerInfo> players = new Dictionary<Guid, PlayerInfo>();

        int playerCounter = 1;
        static string PlayerNameMap(int value)
        {
            char[] baseChars = new char[] { '0','1','2','3','4','5','6','7','8','9',
            'A','B','C','D','E','F','G','H','I','J','K','L','M','N','O','P','Q','R','S','T','U','V','W','X','Y','Z',
            'a','b','c','d','e','f','g','h','i','j','k','l','m','n','o','p','q','r','s','t','u','v','w','x','y','z'};

            MyAssert.Assert(value >= 0);

            string result = string.Empty;
            int targetBase = baseChars.Length;

            do
            {
                result = baseChars[value % targetBase] + result;
                value = value / targetBase;
            }
            while (value > 0);

            return result;
        }

        RemoteActionRepository remoteActions = new RemoteActionRepository();
        HashSet<Guid> playerLocks = new HashSet<Guid>();
        HashSet<Point> worldLocks = new HashSet<Point>();

        public OverlayEndpoint Address { get { return Host.Address; } }

        int serverSpawnDensity;

        public Server(GlobalHost globalHost, ActionSyncronizer sync, int serverSpawnDensity)
        {
            Host = globalHost.NewHost(Server.hostName, Game.Convert(AssignProcessor),
                BasicInfo.GenerateHandshake(NodeRole.SERVER), Aggregator.shortInactivityWait);

            base.serverHost = Address;

            this.serverSpawnDensity = serverSpawnDensity;
        }

        //GameNodeProcessors AssignProcessor(Node n, MemoryStream nodeInfo)
        //{
        //    NodeRole role = Serializer.Deserialize<NodeRole>(nodeInfo);

        //    if (role == NodeRole.CLIENT)
        //        return new GameNodeProcessors(ProcessClientMessage, ClientDisconnect);

        //    if (role == NodeRole.WORLD_VALIDATOR)
        //        return new GameNodeProcessors(ProcessWorldMessage, WorldDisconnect);

        //    if (role == NodeRole.PLAYER_AGENT)
        //    {
        //        PlayerInfo inf = Serializer.Deserialize<PlayerInfo>(nodeInfo);
        //        return new GameNodeProcessors
        //            (
        //                (mt, stm, nd) => ProcessPlayerMessage(mt, stm, nd, inf.id),
        //                PlayerDisconnect
        //            );
        //    }

        //    if (role == NodeRole.PLAYER_VALIDATOR)
        //    {
        //        PlayerInfo inf = Serializer.Deserialize<PlayerInfo>(nodeInfo);
        //        return new GameNodeProcessors
        //            (
        //                (mt, stm, nd) => ProcessPlayerValidatorMessage(mt, stm, nd, inf),
        //                PlayerValidatorDisconnect
        //            );
        //    }

        //    throw new Exception(Log.StDump(n.info, role, "unexpected"));
        //}

        protected override bool AuthorizeClient(Node n) { return true; }
        protected override bool AuthorizeWorld(Node n, WorldInfo inf) { return true; }
        protected override bool AuthorizePlayerAgent(Node n, PlayerInfo inf) { return true; }
        protected override bool AuthorizePlayerValidator(Node n, PlayerInfo inf) { return true; }

        protected override void ProcessClientMessage(MessageType mt, Stream stm, Node n)
        {
            if (mt == MessageType.NEW_PLAYER_REQUEST)
            {
                Guid player = Serializer.Deserialize<Guid>(stm);
                OnNewPlayerRequest(player, n.info.remote);
            }
            else if (mt == MessageType.NEW_WORLD_REQUEST)
            {
                Point worldPos = Serializer.Deserialize<Point>(stm);
                OnNewWorldRequest(worldPos, null, 0);
            }
            else if (mt == MessageType.NEW_VALIDATOR)
            {
                OnNewValidator(n.info.remote.addr);
            }
            else if (mt == MessageType.RESPONSE)
                RemoteAction.Process(remoteActions, n, stm);
            else if (mt == MessageType.STOP_VALIDATING)
                OnStopValidating(n);
            else
                throw new Exception("Client.ProcessClientMessage bad message type " + mt.ToString());
        }
        protected override void ProcessWorldMessage(MessageType mt, Stream stm, Node n, WorldInfo inf)
        {
            if (mt == MessageType.NEW_WORLD_REQUEST)
            {
                Point worldPos = Serializer.Deserialize<Point>(stm);
                OnNewWorldRequest(worldPos, null, 0);
            }
            else if (mt == MessageType.WORLD_HOST_DISCONNECT)
            {
                WorldInitializer w = Serializer.Deserialize<WorldInitializer>(stm);
                OnWorldHostDisconnect(w);
            }
            else
                throw new Exception(Log.StDump("bad message type", mt));
        }
        protected override void ProcessPlayerAgentMessage(MessageType mt, Stream stm, Node n, PlayerInfo inf)
        {
            if (mt == MessageType.SPAWN_REQUEST)
                OnSpawnRequest(inf.id);
            else
                throw new Exception(Log.StDump("unexpected", mt));
        }
        protected override void ProcessPlayerValidatorMessage(MessageType mt, Stream stm, Node n, PlayerInfo inf)
        {
            if (mt == MessageType.PLAYER_HOST_DISCONNECT)
            {
                PlayerData pd = Serializer.Deserialize<PlayerData>(stm);
                OnPlayerHostDisconnect(inf, pd);
            }
            else
                throw new Exception(Log.StDump("bad message type", mt));
        }

        void NewPlayerProcess(IPEndPoint clientAddr, Guid id, int generation, PlayerData pd)
        {
            MyAssert.Assert(!playerLocks.Contains(id));
            ManualLock<Guid> lck = new ManualLock<Guid>(playerLocks, id);

            if (!validatorPool.Any())
                throw new Exception("no validators!");

            string playerName = PlayerNameMap(playerCounter++);
            string fullName = "player " + playerName + (generation == 0 ? "" : " (" + generation + ")");

            OverlayEndpoint validatorHost = new OverlayEndpoint(validatorPool.Random(n => r.Next(n)),
                new OverlayHostName("validator " + fullName));

            OverlayEndpoint playerNewHost = new OverlayEndpoint(clientAddr, new OverlayHostName("agent " + fullName));

            OverlayEndpoint playerClient = new OverlayEndpoint(clientAddr, Client.hostName);
            OverlayEndpoint validatorClient = new OverlayEndpoint(validatorHost.addr, Client.hostName);

            PlayerInfo playerInfo = new PlayerInfo(id, playerNewHost, validatorHost, playerName, generation);

            RemoteAction
                .Send(Host, validatorClient, ProcessClientDisconnect, MessageType.PLAYER_VALIDATOR_ASSIGN, playerInfo, pd)
                .Respond(remoteActions, lck, (res, stm) =>
                {
                    if (playerInfo.generation == 0)
                    {
                        MyAssert.Assert(!players.ContainsKey(playerInfo.id));
                        players.Add(playerInfo.id, playerInfo);
                    }
                    else
                    {
                        MyAssert.Assert(players.ContainsKey(playerInfo.id));
                        players[playerInfo.id] = playerInfo;
                    }

                    MessageClient(playerClient, MessageType.NEW_PLAYER_REQUEST_SUCCESS, playerInfo);

                    Log.Console("New player " + playerInfo.name + " validated by " + playerInfo.validatorHost.addr);
                });
        }

        void OnNewPlayerRequest(PlayerInfo inf, PlayerData pd)
        {
            MyAssert.Assert(players.ContainsKey(inf.id));
            NewPlayerProcess(inf.playerHost.addr, inf.id, inf.generation + 1, pd);
        }
        void OnNewPlayerRequest(Guid playerId, OverlayEndpoint playerClient)
        {
            MyAssert.Assert(!players.ContainsKey(playerId));
            NewPlayerProcess(playerClient.addr, playerId, 0, new PlayerData());
        }

        void OnNewValidator(IPEndPoint ip)
        {
            MyAssert.Assert(!validatorPool.Where((valip) => valip == ip).Any());
            validatorPool.Add(ip);
        }

        MyColor RandomColor(Point p)
        {
            float fScale = .1f;
            Point pShift = new Point(10, 10);

            Func<Point, byte> gen = (pos) => Convert.ToByte(Math.Round((Noise.Generate((float)pos.x * fScale, (float)pos.y * fScale) + 1f) / 2 * 255));

            //p += pShift;
            Byte R = gen(p);

            p += pShift;
            Byte G = gen(p);

            p += pShift;
            Byte B = gen(p);

            return new MyColor(R, G, B);
        }

        void OnNewWorldRequest(Point worldPos, WorldSerialized ser, int generation)
        {
            if (worlds.ContainsKey(worldPos))
            {
                Log.Dump(worldPos, "world alrady present");
                return;
            }

            if (!validatorPool.Any())
                throw new Exception("no validators!");

            ManualLock<Point> lck = new ManualLock<Point>(worldLocks, worldPos);

            if (!lck.Locked)
            {
                Log.Dump(worldPos, "can't work, locked");
                return;
            }

            string hostName = "host world " + worldPos;
            if(generation != 0)
                hostName = hostName + " (" + generation + ")";
            OverlayEndpoint validatorHost = new OverlayEndpoint(validatorPool.Random(n => r.Next(n)), new OverlayHostName(hostName));

            WorldInitializer init;
            WorldInfo newWorld;
            bool hasSpawn = false;

            if (ser == null)
            {
                WorldSeed seed = new WorldSeed(r.Next(), RandomColor(worldPos));

                if (serverSpawnDensity == 0)
                {
                    if (worldPos == Point.Zero)
                        hasSpawn = true;
                }
                else if ((worldPos.x % serverSpawnDensity == 0) && (worldPos.y % serverSpawnDensity == 0))
                {
                    hasSpawn = true;
                }

                newWorld = new WorldInfo(worldPos, validatorHost, generation, hasSpawn);
                init = new WorldInitializer(newWorld, seed);
            }
            else
            {
                hasSpawn = ser.spawnPos.HasValue;
                newWorld = new WorldInfo(worldPos, validatorHost, generation, hasSpawn);
                init = new WorldInitializer(newWorld, ser);
            }


            OverlayEndpoint validatorClient = new OverlayEndpoint(validatorHost.addr, Client.hostName);

            RemoteAction
                .Send(Host, validatorClient, ProcessClientDisconnect, MessageType.WORLD_VALIDATOR_ASSIGN, init)
                .Respond(remoteActions, lck, (res, stm) =>
                {
                    if(res != Response.SUCCESS)
                        throw new Exception( Log.StDump("unexpected", res) );
                    
                    //if (hasSpawn == true)
                    //    spawnWorlds.Add(worldPos);

                    worlds.Add(newWorld.position, newWorld);

                    //Log.LogWriteLine("New world " + worldPos + " validated by " + validatorHost.addr);

                    foreach (Point p in Point.SymmetricRange(Point.One))
                    {
                        if (p == Point.Zero)
                            continue;

                        Point neighborPos = p + newWorld.position;

                        if (!worlds.ContainsKey(neighborPos))
                            continue;

                        WorldInfo neighborWorld = worlds[neighborPos];

                        MessageWorld(neighborWorld, MessageType.NEW_NEIGHBOR, newWorld);
                        MessageWorld(newWorld, MessageType.NEW_NEIGHBOR, neighborWorld);
                    }
                });

        }

        void OnSpawnRequest(Guid playerId)
        {
            MyAssert.Assert(players.ContainsKey(playerId));
            PlayerInfo inf = players[playerId];

            var spawnWorlds = ( from wi in worlds.Values
                                where wi.hasSpawn
                                select wi.position).ToList();
            
            if (!spawnWorlds.Any())
            {
                Log.Dump("No spawn worlds", inf);
                return;
            }

            Point spawnWorldPos = spawnWorlds.Random(n => r.Next(n));

            WorldInfo spawnWorld = worlds.GetValue(spawnWorldPos);

            MessageWorld(spawnWorld, MessageType.SPAWN_REQUEST, inf);
        }

        void OnStopValidating(Node n)
        {
            IPEndPoint addr = n.info.remote.addr;
            
            MyAssert.Assert(validatorPool.Contains(addr));
            validatorPool.Remove(addr);

            Log.Dump(n.info.remote);
        }

        void OnWorldHostDisconnect(WorldInitializer w)
        {
            //Log.Dump(mt, w.info);
            worlds.Remove(w.info.position);
            OnNewWorldRequest(w.info.position, w.world, w.info.generation + 1);
        }

        void OnPlayerHostDisconnect(PlayerInfo inf, PlayerData pd)
        {
            Log.Dump(inf, pd);
            OnNewPlayerRequest(inf, pd);
        }

        public void PrintStats()
        {
            Host.PrintStats();
        }
    }
}
