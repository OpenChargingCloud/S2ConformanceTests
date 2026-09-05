//! A stand-in for the `zeroconf-tokio` crate used by `s2energy-connection::discovery`.
//!
//! The real crate binds to Avahi (Linux) or Bonjour (macOS/Windows) through `bonjour-sys`,
//! which needs libclang and the Apple Bonjour SDK to build on Windows. mDNS discovery is not
//! part of the interoperability tests (WWCP_S2 talks to the s2-rust pairing and communication
//! servers through explicit URLs), so this crate provides the exact API surface the discovery
//! module compiles against and reports "not available" when it is used at run time.
//! It is wired in through `[patch.crates-io]` in the harness manifest and never touches the
//! s2-rust submodule.

use std::collections::HashMap;
use std::fmt;

pub mod error {
    /// The error of the stubbed mDNS layer.
    #[derive(Debug, Clone)]
    pub struct Error(pub String);

    impl std::fmt::Display for Error {
        fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
            f.write_str(&self.0)
        }
    }

    impl std::error::Error for Error {}
}

pub type Result<T> = std::result::Result<T, error::Error>;

fn unavailable() -> error::Error {
    error::Error("mDNS discovery is not available in the interop harness (zeroconf-tokio is stubbed out)".into())
}

/// A DNS-SD service type with optional sub types, e.g. `_s2connect._tcp` with `_cem`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ServiceType {
    name: String,
    protocol: String,
    sub_types: Vec<String>,
}

impl ServiceType {
    pub fn new(name: &str, protocol: &str) -> Result<Self> {
        Self::with_sub_types(name, protocol, vec![])
    }

    pub fn with_sub_types(name: &str, protocol: &str, sub_types: Vec<&str>) -> Result<Self> {
        Ok(Self {
            name: name.to_string(),
            protocol: protocol.to_string(),
            sub_types: sub_types.into_iter().map(|s| s.to_string()).collect(),
        })
    }

    pub fn name(&self) -> &str {
        &self.name
    }

    pub fn protocol(&self) -> &str {
        &self.protocol
    }

    pub fn sub_types(&self) -> &Vec<String> {
        &self.sub_types
    }
}

impl fmt::Display for ServiceType {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "_{}._{}", self.name, self.protocol)
    }
}

/// A TXT record (key/value pairs).
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct TxtRecord(HashMap<String, String>);

impl<K: AsRef<str>, V: AsRef<str>> From<HashMap<K, V>> for TxtRecord {
    fn from(map: HashMap<K, V>) -> Self {
        Self(map.into_iter().map(|(k, v)| (k.as_ref().to_string(), v.as_ref().to_string())).collect())
    }
}

pub mod prelude {
    pub trait TTxtRecord: Clone + PartialEq + Eq + std::fmt::Debug {
        fn new() -> Self;
        fn insert(&mut self, key: &str, value: &str) -> crate::Result<()>;
        fn get(&self, key: &str) -> Option<String>;
        fn remove(&mut self, key: &str) -> crate::Result<()>;
        fn contains_key(&self, key: &str) -> bool;
        fn len(&self) -> usize;
        fn is_empty(&self) -> bool {
            self.len() == 0
        }
    }

    pub trait TMdnsService {
        fn new(service_type: crate::ServiceType, port: u16) -> Self;
        fn set_name(&mut self, name: &str);
        fn set_txt_record(&mut self, txt_record: crate::TxtRecord);
    }

    pub trait TMdnsBrowser {
        fn new(service_type: crate::ServiceType) -> Self;
    }
}

impl prelude::TTxtRecord for TxtRecord {
    fn new() -> Self {
        Self::default()
    }

    fn insert(&mut self, key: &str, value: &str) -> Result<()> {
        self.0.insert(key.to_string(), value.to_string());
        Ok(())
    }

    fn get(&self, key: &str) -> Option<String> {
        self.0.get(key).cloned()
    }

    fn remove(&mut self, key: &str) -> Result<()> {
        self.0.remove(key);
        Ok(())
    }

    fn contains_key(&self, key: &str) -> bool {
        self.0.contains_key(key)
    }

    fn len(&self) -> usize {
        self.0.len()
    }
}

/// A service to advertise (never actually published).
#[derive(Debug, Clone)]
pub struct MdnsService {
    pub service_type: ServiceType,
    pub port: u16,
    pub name: Option<String>,
    pub txt_record: Option<TxtRecord>,
}

impl prelude::TMdnsService for MdnsService {
    fn new(service_type: ServiceType, port: u16) -> Self {
        Self { service_type, port, name: None, txt_record: None }
    }

    fn set_name(&mut self, name: &str) {
        self.name = Some(name.to_string());
    }

    fn set_txt_record(&mut self, txt_record: TxtRecord) {
        self.txt_record = Some(txt_record);
    }
}

/// A browser of a service type (never actually browsing).
#[derive(Debug, Clone)]
pub struct MdnsBrowser {
    pub service_type: ServiceType,
}

impl prelude::TMdnsBrowser for MdnsBrowser {
    fn new(service_type: ServiceType) -> Self {
        Self { service_type }
    }
}

/// The registration of an advertised service.
#[derive(Debug, Clone)]
pub struct ServiceRegistration;

/// The asynchronous advertiser; `start` reports that mDNS is unavailable.
pub struct MdnsServiceAsync {
    _service: MdnsService,
}

impl MdnsServiceAsync {
    pub fn new(service: MdnsService) -> Result<Self> {
        Ok(Self { _service: service })
    }

    pub async fn start(&mut self) -> Result<ServiceRegistration> {
        Err(unavailable())
    }

    pub async fn shutdown(&mut self) -> Result<()> {
        Ok(())
    }
}

/// A discovered service.
#[derive(Debug, Clone)]
pub struct ServiceDiscovery {
    name: String,
    service_type: ServiceType,
    txt: Option<TxtRecord>,
}

impl ServiceDiscovery {
    pub fn name(&self) -> &String {
        &self.name
    }

    pub fn service_type(&self) -> &ServiceType {
        &self.service_type
    }

    pub fn txt(&self) -> &Option<TxtRecord> {
        &self.txt
    }
}

/// A service that went away.
#[derive(Debug, Clone)]
pub struct ServiceRemoval {
    name: String,
}

impl ServiceRemoval {
    pub fn name(&self) -> &String {
        &self.name
    }
}

/// A browsing event.
#[derive(Debug, Clone)]
pub enum BrowserEvent {
    Add(ServiceDiscovery),
    Remove(ServiceRemoval),
}

/// The asynchronous browser; `start` and `next` report that mDNS is unavailable.
pub struct MdnsBrowserAsync {
    _browser: MdnsBrowser,
}

impl MdnsBrowserAsync {
    pub fn new(browser: MdnsBrowser) -> Result<Self> {
        Ok(Self { _browser: browser })
    }

    pub async fn start(&mut self) -> Result<()> {
        Err(unavailable())
    }

    pub async fn next(&mut self) -> Option<Result<BrowserEvent>> {
        Some(Err(unavailable()))
    }

    pub async fn shutdown(&mut self) -> Result<()> {
        Ok(())
    }
}
