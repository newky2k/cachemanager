﻿using DSoft.CacheManager.LiteDB;
using LiteDB;
using Microsoft.Extensions.Options;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace DSoft.CacheManager
{
    public class CacheManager : ICacheManager
    {
        private const string CacheItemCollectionName = "CacheItems";

        #region Fields
        private CacheConfiguration _configuration;
        private CacheManagerItemCollection _cachedItems;
        private bool _isLoaded;
        private Dictionary<string, object> _dataDictionary = new Dictionary<string, object>();
        private object getItemLock = new object();
        private IStorageBackend _backend;

        #endregion

        #region Properties

        #region Private Properties
        private CacheConfiguration Configuration => _configuration;

        private CacheManagerItemCollection Cache
        {
            get
            {
                if (_cachedItems == null)
                {
                    _cachedItems = LoadCache();

                    _isLoaded = true;
                }


                return _cachedItems;
            }
            set
            {
                _cachedItems = value;
            }
        }

        private string BasePath => _configuration.Location;

        private string StoragePath => Path.Combine(BasePath, Configuration.FileName);

        private IStorageBackend BackEnd
        {
            get
            {
                if (_backend == null)
                    _backend = new LiteDbBackend() { FileName = Configuration.FileName, Location = _configuration.Location, Password = Configuration.Password };

                return _backend;
            }
        }

        #endregion

        #endregion

        #region Constructors

        public CacheManager(CacheConfiguration configuration)
        {
            _configuration = configuration;
        }

        public CacheManager(IOptions<CacheConfiguration> options) : this(options.Value)
        {

        }

        #endregion

        #region Methods

        /// <summary>
        /// Determines whether the specified key is registered
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns>
        ///   <c>true</c> if  specified key is registered; otherwise, <c>false</c>.
        /// </returns>
        public bool IsKeyRegistered(string key) => Cache.ContainsKey(key);

        public List<T> GetItems<T>(string key)
        {
            if (!Cache.ContainsKey(key))
                throw new Exception($"No data registered with key: {key} in the CacheManager");

            if (!_dataDictionary.ContainsKey(key) || _dataDictionary[key] == null)
            {
                lock (getItemLock) //handle double loading
                {
                    //need to load the data from the database
                    if (!BackEnd.CacheEntryExists(key))
                        throw new Exception($"No data registered with key: {key} in the CacheManager");

                    if (_dataDictionary.ContainsKey(key) && _dataDictionary[key] != null)
                        return (List<T>)_dataDictionary[key]; //handle check after the first lock has completed

                    var data = BackEnd.GetItems<T>(key);

                    if (_dataDictionary.ContainsKey(key))
                        _dataDictionary[key] = data;
                    else
                        _dataDictionary.Add(key, data);
                }

            }

            return (List<T>)_dataDictionary[key];
        }

        public void SetItems<T>(string key, List<T> content, DateTime? lastUpdated = null)
        {
             var dTime = lastUpdated.HasValue ? lastUpdated : DateTime.Now;

            if (!Cache.ContainsKey(key))
            {
                var newItem = AddNewKey<T>(key, dTime.Value);

                Cache.Add(newItem);
            }
            else
            {
                //replace
                Cache[key].LastUpdated = dTime;
            }

            if (_dataDictionary.ContainsKey(key))
                _dataDictionary[key] = content;
            else
                _dataDictionary.Add(key, content);

            UpdateStoredCache(key, content, dTime.Value);

        }

        public DateTime? GetLastUpdated(string key)
        {
            if (!Cache.ContainsKey(key))
                throw new Exception($"No data registered with key: {key} in the CacheManager");

            return Cache[key].LastUpdated;

        }

        public void UpdateContentsLastUpdated(string key, DateTime? lastUpdated = null)
        {
            if (!Cache.ContainsKey(key))
                throw new Exception($"No data registered with key: {key} in the CacheManager");

            var dTime = (lastUpdated.HasValue) ? lastUpdated : DateTime.Now;

            Cache[key].LastUpdated = dTime;

            UpdateCacheKeys(key, dTime.Value);
        }

        public async Task LoadAsync()
        {
            await Task.Run(() =>
            {
                Cache = LoadCache();

                _isLoaded = true;
            });

        }

        public async Task SyncroniseAsync()
        {
            await Task.Run(() =>
            {
                SaveCache();
            });

        }

        public void Synronise()
        {
            Task.Factory.StartNew(() =>
            {
                SaveCache();
            });

        }

        public void ResetCache()
        {
            try
            {
                if (File.Exists(StoragePath))
                    File.Delete(StoragePath);
            }
            catch (Exception)
            {

                throw;
            }
        }


        #endregion

        #region Private Methods

        private CacheManagerItem AddNewKey<T>(string key, DateTime updateTime)
        {
            var col = BackEnd.Database.GetCollection<CacheManagerItem>(CacheItemCollectionName);

            var existingItem = col.FindOne(x => x.Key.Equals(key));

            if (existingItem != null)
            {
                existingItem.LastUpdated = updateTime;
                existingItem.Type = typeof(T).AssemblyQualifiedName;
                existingItem.ListType = typeof(List<T>).AssemblyQualifiedName;

                col.Update(existingItem);
            }
            else
            {
                existingItem = new CacheManagerItem()
                {
                    Key = key,
                    LastUpdated = updateTime,
                    Type = typeof(T).AssemblyQualifiedName,
                    ListType = typeof(List<T>).AssemblyQualifiedName,
                };


                col.Insert(existingItem);
            }

            col.EnsureIndex(x => x.Key);

            return existingItem;

        }

        private void UpdateCacheKeys(string key, DateTime updateTime)
        {
            var col = BackEnd.Database.GetCollection<CacheManagerItem>(CacheItemCollectionName);


            var existingItem = col.FindOne(x => x.Key.Equals(key));

            if (existingItem != null)
            {
                existingItem.LastUpdated = updateTime;

                col.Update(existingItem);
            }
            else
            {
                var newKey = new CacheManagerItem()
                {
                    Key = key,
                    LastUpdated = updateTime,
                };

                col.Insert(newKey);
            }

            col.EnsureIndex(x => x.Key);
        }

        private void UpdateStoredCache<T>(string key, List<T> content, DateTime updateTime)
        {
            UpdateCacheKeys(key, updateTime);

            if (BackEnd.Database.CollectionExists(key))
                BackEnd.Database.DropCollection(key);

            var ccol = BackEnd.Database.GetCollection<T>(key);
            ccol.InsertBulk(content);
            //Database.FileStorage.
        }

        private CacheManagerItemCollection LoadCache()
        {
            try
            {

                if (!Directory.Exists(BasePath))
                    Directory.CreateDirectory(BasePath);

                var items = new CacheManagerItemCollection();

                if (!File.Exists(StoragePath))
                    return items;

                //load the keys into memory, other items can be loaded as an when
                LoadKeys(items);


                //try loading the stored data too
                foreach (var aKey in items)
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(aKey.Type))
                        {
                            var aType = Type.GetType(aKey.Type);

                            var ex = typeof(IStorageBackend);
                            var mi = ex.GetMethod(nameof(IStorageBackend.GetItems));
                            var miConstructed = mi.MakeGenericMethod(aType);
                            
                            var dList = (IList)miConstructed.Invoke(BackEnd, new object[] { aKey.Key });

                            ////old way?
                            //var dList = (IList)Activator.CreateInstance(dType);

                            //var count = dList.Count;

                            //var col = BackEnd.Database.GetCollection(aKey.Key);

                            //var mapper = BsonMapper.Global;

                            //foreach (var aItem in col.FindAll())
                            //{
                            //    var newItem = mapper.ToObject(Type.GetType(aKey.Type), aItem);

                            //    dList.Add(newItem);
                            //}

                            if (_dataDictionary.ContainsKey(aKey.Key))
                                _dataDictionary[aKey.Key] = dList;
                            else
                                _dataDictionary.Add(aKey.Key, dList);


                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);

                    }


                }


                return items;


            }
            catch (Exception)
            {

                return new CacheManagerItemCollection();
            }


        }

        private void LoadKeys(CacheManagerItemCollection tree)
        {
            var allKeys = BackEnd.GetItems<CacheManagerItem>(CacheItemCollectionName);

            tree.AddRange(allKeys);

        }

        private void SaveCache()
        {
            try
            {
                //LiteDb saves on update
                
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);

            }
        }

        public void Dispose()
        {
            _backend = null;
            _configuration = null;
        }

        #endregion

    }
}
