use std::collections::HashMap;
use std::io::{self, Write};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use anyhow::{Context as _, Result as AnyResult};
use bytes::Bytes;
use haste::broadcast::{BroadcastHttp, BroadcastHttpClientError, HttpClient, default_headers};
use haste::entities::{DeltaHeader, Entity, fkey_from_path};
use haste::fxhash;
use haste::packet_channel_broadcast_stream::PacketChannelBroadcastStream;
use haste::parser::{AsyncStreamingParser, Context, Visitor};
use serde::Serialize;
use tokio::sync::{mpsc, oneshot};

const PLAYER_CONTROLLER_CLASS_HASH: u64 = fxhash::hash_bytes(b"CCitadelPlayerController");
const STEAM_ID_HASH: u64 = fxhash::hash_bytes(b"m_steamID");
const HERO_ID_HASH: u64 = fkey_from_path(&["m_PlayerDataGlobal", "m_nHeroID"]);
const HERO_DAMAGE_HASH: u64 = fkey_from_path(&["m_PlayerDataGlobal", "m_iHeroDamage"]);
const HEARTBEAT_INTERVAL: Duration = Duration::from_secs(30);
const INITIAL_RELAY_RETRY_DELAY: Duration = Duration::from_secs(2);
const MAXIMUM_RELAY_RETRY_DELAY: Duration = Duration::from_secs(5);
const RELAY_CONNECT_TIMEOUT: Duration = Duration::from_secs(10);
const RELAY_BOOTSTRAP_ATTEMPT_TIMEOUT: Duration = Duration::from_secs(60);
const RELAY_STREAM_PACKET_TIMEOUT: Duration = Duration::from_secs(60);
const INITIAL_STREAM_RECONNECT_DELAY: Duration = Duration::from_secs(2);
const MAXIMUM_STREAM_RECONNECT_DELAY: Duration = Duration::from_secs(30);
const STABLE_STREAM_DELTA_PACKETS: u64 = 3;

#[derive(Debug)]
struct Arguments {
    match_id: u64,
    broadcast_url: String,
}

#[derive(Debug, thiserror::Error)]
enum OutputError {
    #[error(transparent)]
    Io(#[from] io::Error),
    #[error(transparent)]
    Json(#[from] serde_json::Error),
}

#[derive(Clone, Copy, Debug, Default, Serialize)]
struct TrafficSnapshot {
    requests_total: u64,
    sync_requests: u64,
    start_requests: u64,
    full_requests: u64,
    delta_requests: u64,
    other_requests: u64,
    responses_2xx: u64,
    responses_404: u64,
    responses_405: u64,
    responses_other: u64,
    transport_errors: u64,
    body_errors: u64,
    decoded_body_bytes: u64,
}

#[derive(Default)]
struct TrafficCounters {
    values: Mutex<TrafficSnapshot>,
}

impl TrafficCounters {
    fn record_request(&self, endpoint: RelayEndpoint) {
        let mut values = self
            .values
            .lock()
            .unwrap_or_else(|error| error.into_inner());

        values.requests_total = values.requests_total.saturating_add(1);
        match endpoint {
            RelayEndpoint::Sync => {
                values.sync_requests = values.sync_requests.saturating_add(1);
            }
            RelayEndpoint::Start => {
                values.start_requests = values.start_requests.saturating_add(1);
            }
            RelayEndpoint::Full => {
                values.full_requests = values.full_requests.saturating_add(1);
            }
            RelayEndpoint::Delta => {
                values.delta_requests = values.delta_requests.saturating_add(1);
            }
            RelayEndpoint::Other => {
                values.other_requests = values.other_requests.saturating_add(1);
            }
        }
    }

    fn record_response(&self, status: http::StatusCode) {
        let mut values = self
            .values
            .lock()
            .unwrap_or_else(|error| error.into_inner());

        match status.as_u16() {
            200..=299 => {
                values.responses_2xx = values.responses_2xx.saturating_add(1);
            }
            404 => {
                values.responses_404 = values.responses_404.saturating_add(1);
            }
            405 => {
                values.responses_405 = values.responses_405.saturating_add(1);
            }
            _ => {
                values.responses_other = values.responses_other.saturating_add(1);
            }
        }
    }

    fn record_transport_error(&self) {
        let mut values = self
            .values
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        values.transport_errors = values.transport_errors.saturating_add(1);
    }

    fn record_body_result(&self, body: &Result<Bytes, reqwest::Error>) {
        let mut values = self
            .values
            .lock()
            .unwrap_or_else(|error| error.into_inner());

        match body {
            Ok(bytes) => {
                values.decoded_body_bytes =
                    values.decoded_body_bytes.saturating_add(bytes.len() as u64);
            }
            Err(_) => {
                values.body_errors = values.body_errors.saturating_add(1);
            }
        }
    }

    fn snapshot(&self) -> TrafficSnapshot {
        *self
            .values
            .lock()
            .unwrap_or_else(|error| error.into_inner())
    }
}

#[derive(Clone, Copy)]
enum RelayEndpoint {
    Sync,
    Start,
    Full,
    Delta,
    Other,
}

impl RelayEndpoint {
    fn from_path(path: &str) -> Self {
        match path.rsplit('/').next().unwrap_or_default() {
            "sync" => Self::Sync,
            "start" => Self::Start,
            "full" => Self::Full,
            "delta" => Self::Delta,
            _ => Self::Other,
        }
    }
}

#[derive(Clone)]
struct CountingHttpClient {
    client: reqwest::Client,
    traffic: Arc<TrafficCounters>,
}

impl HttpClient for CountingHttpClient {
    type Error = reqwest::Error;

    async fn execute(
        &self,
        request: http::Request<Bytes>,
    ) -> Result<http::Response<Result<Bytes, Self::Error>>, Self::Error> {
        self.traffic
            .record_request(RelayEndpoint::from_path(request.uri().path()));

        let (parts, body) = request.into_parts();
        let mut request = self
            .client
            .request(parts.method, parts.uri.to_string())
            .body(body)
            .headers(parts.headers);

        #[cfg(not(target_arch = "wasm32"))]
        {
            request = request.version(parts.version);
        }

        let mut response = match request.send().await {
            Ok(value) => value,
            Err(error) => {
                self.traffic.record_transport_error();
                return Err(error);
            }
        };

        self.traffic.record_response(response.status());

        let mut result = http::Response::builder().status(response.status());
        #[cfg(not(target_arch = "wasm32"))]
        {
            result = result.version(response.version());
        }

        std::mem::swap(
            response.headers_mut(),
            result
                .headers_mut()
                .expect("could not get response headers"),
        );

        let body = response.bytes().await;
        self.traffic.record_body_result(&body);

        Ok(result
            .body(body)
            .expect("could not construct counted HTTP response"))
    }
}

#[derive(Serialize)]
struct ReadyEvent<'a> {
    #[serde(rename = "type")]
    event_type: &'static str,
    match_id: u64,
    broadcast_url: &'a str,
    parser_version: &'static str,
    relay_attempts: u32,
    bootstrap_duration_ms: u64,
    traffic: TrafficSnapshot,
}

#[derive(Serialize)]
struct RelayWaitEvent<'a> {
    #[serde(rename = "type")]
    event_type: &'static str,
    match_id: u64,
    attempt: u32,
    retry_delay_ms: u64,
    reason: &'a str,
    traffic: TrafficSnapshot,
}

#[derive(Serialize)]
struct PlayerDamageEvent {
    #[serde(rename = "type")]
    event_type: &'static str,
    match_id: u64,
    tick: i32,
    steam_id64: u64,
    account_id: u32,
    hero_id: u32,
    hero_damage: i32,
}

#[derive(Serialize)]
struct HeartbeatEvent {
    #[serde(rename = "type")]
    event_type: &'static str,
    match_id: u64,
    tick: i32,
    tracked_players: usize,
    traffic: TrafficSnapshot,
}

#[derive(Serialize)]
struct ErrorEvent<'a> {
    #[serde(rename = "type")]
    event_type: &'static str,
    match_id: u64,
    message: &'a str,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct LastPlayerValue {
    hero_id: u32,
    hero_damage: i32,
    tick: i32,
}

struct DamageVisitor {
    match_id: u64,
    last_values: HashMap<u32, LastPlayerValue>,
    highest_tick: i32,
    last_heartbeat_at: Instant,
    traffic: Arc<TrafficCounters>,
}

struct StreamReconnect {
    reason: String,
    delta_packets: u64,
}

impl DamageVisitor {
    fn new(match_id: u64, traffic: Arc<TrafficCounters>) -> Self {
        Self {
            match_id,
            last_values: HashMap::new(),
            highest_tick: 0,
            last_heartbeat_at: Instant::now(),
            traffic,
        }
    }
}

impl Visitor for DamageVisitor {
    type Error = OutputError;

    fn should_track_entity(&self, serializer_name_hash: u64) -> bool {
        serializer_name_hash == PLAYER_CONTROLLER_CLASS_HASH
    }

    async fn on_entity(
        &mut self,
        ctx: &Context,
        delta_header: DeltaHeader,
        entity: &Entity,
    ) -> Result<(), Self::Error> {
        if delta_header == DeltaHeader::DELETE || delta_header == DeltaHeader::LEAVE {
            return Ok(());
        }

        if entity.serializer().serializer_name.hash != PLAYER_CONTROLLER_CLASS_HASH {
            return Ok(());
        }

        let steam_id64: Option<u64> = entity.get_value(&STEAM_ID_HASH);
        let hero_id: Option<u32> = entity.get_value(&HERO_ID_HASH);
        let hero_damage: Option<i32> = entity.get_value(&HERO_DAMAGE_HASH);

        let (Some(steam_id64), Some(hero_id), Some(hero_damage)) =
            (steam_id64, hero_id, hero_damage)
        else {
            return Ok(());
        };

        if steam_id64 == 0 || hero_id == 0 {
            return Ok(());
        }

        let account_id = (steam_id64 & 0xFFFF_FFFF) as u32;
        if account_id == 0 {
            return Ok(());
        }

        let tick = ctx.tick();
        self.highest_tick = self.highest_tick.max(tick);
        let value = LastPlayerValue {
            hero_id,
            hero_damage: hero_damage.max(0),
            tick,
        };

        if let Some(previous) = self.last_values.get_mut(&account_id) {
            if tick < previous.tick {
                return Ok(());
            }

            if previous.hero_id == value.hero_id && previous.hero_damage == value.hero_damage {
                previous.tick = tick;
                return Ok(());
            }
        }

        self.last_values.insert(account_id, value);

        emit_json_line(&PlayerDamageEvent {
            event_type: "player_damage",
            match_id: self.match_id,
            tick,
            steam_id64,
            account_id,
            hero_id,
            hero_damage: value.hero_damage,
        })?;

        Ok(())
    }

    async fn on_tick_end(&mut self, ctx: &Context) -> Result<(), Self::Error> {
        let tick = ctx.tick();
        if tick < self.highest_tick {
            return Ok(());
        }
        self.highest_tick = tick;

        if self.last_heartbeat_at.elapsed() < HEARTBEAT_INTERVAL {
            return Ok(());
        }

        self.last_heartbeat_at = Instant::now();
        emit_json_line(&HeartbeatEvent {
            event_type: "heartbeat",
            match_id: self.match_id,
            tick,
            tracked_players: self.last_values.len(),
            traffic: self.traffic.snapshot(),
        })?;
        Ok(())
    }
}

#[tokio::main]
async fn main() {
    let arguments = match parse_arguments() {
        Ok(value) => value,
        Err(error) => {
            eprintln!("{error:#}");
            std::process::exit(2);
        }
    };

    if let Err(error) = run(&arguments).await {
        let message = format!("{error:#}");
        let _ = emit_json_line(&ErrorEvent {
            event_type: "error",
            match_id: arguments.match_id,
            message: &message,
        });
        eprintln!("{message}");
        std::process::exit(1);
    }
}

async fn run(arguments: &Arguments) -> AnyResult<()> {
    let mut parent_closed = monitor_parent_pipe();
    let traffic = Arc::new(TrafficCounters::default());

    let relay_headers =
        default_headers(1_422_450).context("failed to create Valve relay request headers")?;

    let mut stream_reconnect_attempt = 0_u32;
    let mut stream_reconnect_delay = INITIAL_STREAM_RECONNECT_DELAY;
    let mut visitor = DamageVisitor::new(arguments.match_id, Arc::clone(&traffic));

    loop {
        let bootstrap_started_at = Instant::now();
        let open_stream = open_broadcast(arguments, &relay_headers, Arc::clone(&traffic));

        let (broadcast, initial_packets, relay_attempts) = tokio::select! {
            result = open_stream => result?,
            parent_result = &mut parent_closed => {
                parent_result.context(
                    "parent pipe monitor stopped unexpectedly",
                )?;
                anyhow::bail!("ThreatHudBridge parent pipe closed");
            }
        };

        emit_json_line(&ReadyEvent {
            event_type: "ready",
            match_id: arguments.match_id,
            broadcast_url: &arguments.broadcast_url,
            parser_version: env!("CARGO_PKG_VERSION"),
            relay_attempts,
            bootstrap_duration_ms: bootstrap_started_at.elapsed().as_millis() as u64,
            traffic: traffic.snapshot(),
        })?;

        let stream_started_at = Instant::now();
        let (stream_reconnect, returned_visitor) =
            run_stream_session(broadcast, initial_packets, visitor, &mut parent_closed).await?;
        visitor = returned_visitor;

        if stream_reconnect.delta_packets >= STABLE_STREAM_DELTA_PACKETS
            || (stream_reconnect.delta_packets > 0
                && stream_started_at.elapsed() >= HEARTBEAT_INTERVAL)
        {
            stream_reconnect_attempt = 0;
            stream_reconnect_delay = INITIAL_STREAM_RECONNECT_DELAY;
        }

        stream_reconnect_attempt = stream_reconnect_attempt.saturating_add(1);
        let reconnect_reason = format!(
            "working relay stream interrupted; rebuilding from /sync: {}",
            stream_reconnect.reason,
        );

        emit_json_line(&RelayWaitEvent {
            event_type: "relay_wait",
            match_id: arguments.match_id,
            attempt: stream_reconnect_attempt,
            retry_delay_ms: stream_reconnect_delay.as_millis() as u64,
            reason: &reconnect_reason,
            traffic: traffic.snapshot(),
        })
        .context("failed to emit stream reconnect event")?;

        tokio::select! {
            _ = tokio::time::sleep(stream_reconnect_delay) => {}
            parent_result = &mut parent_closed => {
                parent_result.context(
                    "parent pipe monitor stopped unexpectedly",
                )?;
                anyhow::bail!("ThreatHudBridge parent pipe closed");
            }
        }

        stream_reconnect_delay = std::cmp::min(
            stream_reconnect_delay.saturating_mul(2),
            MAXIMUM_STREAM_RECONNECT_DELAY,
        );
    }
}

async fn open_broadcast(
    arguments: &Arguments,
    relay_headers: &http::HeaderMap,
    traffic: Arc<TrafficCounters>,
) -> AnyResult<(BroadcastHttp<'static, CountingHttpClient>, [Bytes; 2], u32)> {
    let mut attempt = 0_u32;
    let mut retry_delay = INITIAL_RELAY_RETRY_DELAY;
    let mut relay_client = create_relay_http_client(relay_headers, Arc::clone(&traffic))?;

    loop {
        attempt = attempt.saturating_add(1);
        let traffic_before_attempt = traffic.snapshot();

        /*
         * A short network outage can invalidate pooled connections and
         * cached routing state. HTTP responses such as 404/405 keep using
         * the existing pool, but a transport/body failure or a timeout
         * replaces it before the next attempt. This refreshes DNS and the
         * route without adding relay requests or needless TCP handshakes.
         * The client from the successful attempt remains owned by
         * BroadcastHttp and is reused for every following fragment.
         */
        let attempt_result = tokio::time::timeout(RELAY_BOOTSTRAP_ATTEMPT_TIMEOUT, async {
            let mut broadcast: BroadcastHttp<'static, CountingHttpClient> =
                BroadcastHttp::start_streaming(
                    relay_client.clone(),
                    arguments.broadcast_url.clone(),
                )
                .await
                .map_err(|error| format_relay_error("/sync", error))?;

            let start_packet = next_required_bootstrap_packet(&mut broadcast, "/start").await?;

            let full_packet = next_required_bootstrap_packet(&mut broadcast, "/full").await?;

            Ok::<_, String>((broadcast, [start_packet, full_packet]))
        })
        .await;

        let should_reset_relay_client = match &attempt_result {
            Err(_) => true,
            Ok(_) => {
                let traffic_after_attempt = traffic.snapshot();
                traffic_after_attempt.transport_errors > traffic_before_attempt.transport_errors
                    || traffic_after_attempt.body_errors > traffic_before_attempt.body_errors
            }
        };

        let attempt_result = attempt_result.unwrap_or_else(|_| {
            Err(format!(
                "relay bootstrap attempt timed out after {} seconds",
                RELAY_BOOTSTRAP_ATTEMPT_TIMEOUT.as_secs(),
            ))
        });

        match attempt_result {
            Ok((broadcast, initial_packets)) => {
                return Ok((broadcast, initial_packets, attempt));
            }
            Err(reason) => {
                emit_json_line(&RelayWaitEvent {
                    event_type: "relay_wait",
                    match_id: arguments.match_id,
                    attempt,
                    retry_delay_ms: retry_delay.as_millis() as u64,
                    reason: &reason,
                    traffic: traffic.snapshot(),
                })
                .context("failed to emit relay wait event")?;

                if should_reset_relay_client {
                    relay_client = create_relay_http_client(relay_headers, Arc::clone(&traffic))?;
                }

                tokio::time::sleep(retry_delay).await;
                retry_delay =
                    std::cmp::min(retry_delay.saturating_mul(2), MAXIMUM_RELAY_RETRY_DELAY);
            }
        }
    }
}

async fn run_stream_session(
    mut broadcast: BroadcastHttp<'static, CountingHttpClient>,
    initial_packets: [Bytes; 2],
    visitor: DamageVisitor,
    parent_closed: &mut oneshot::Receiver<()>,
) -> AnyResult<(StreamReconnect, DamageVisitor)> {
    let (packet_tx, packet_rx) = mpsc::channel::<Bytes>(32);
    let packet_traffic = Arc::clone(&visitor.traffic);
    let pump_task = tokio::spawn(async move {
        for packet in initial_packets {
            if packet_tx.send(packet).await.is_err() {
                return StreamReconnect {
                    reason: "broadcast parser packet channel closed during bootstrap".to_owned(),
                    delta_packets: 0,
                };
            }
        }

        let mut delta_packets = 0_u64;
        loop {
            let packet_result =
                match tokio::time::timeout(RELAY_STREAM_PACKET_TIMEOUT, broadcast.next_packet())
                    .await
                {
                    Ok(value) => value,
                    Err(_) => {
                        packet_traffic.record_transport_error();
                        return StreamReconnect {
                            reason: format!(
                                "relay /delta timed out after {} seconds",
                                RELAY_STREAM_PACKET_TIMEOUT.as_secs(),
                            ),
                            delta_packets,
                        };
                    }
                };

            match packet_result {
                Some(Ok(packet)) => {
                    if packet_tx.send(packet).await.is_err() {
                        return StreamReconnect {
                            reason: "broadcast parser packet channel closed unexpectedly"
                                .to_owned(),
                            delta_packets,
                        };
                    }
                    delta_packets = delta_packets.saturating_add(1);
                }
                Some(Err(error)) => {
                    return StreamReconnect {
                        reason: format_relay_error("/delta", error),
                        delta_packets,
                    };
                }
                None => {
                    return StreamReconnect {
                        reason: "relay /delta became unavailable after internal retries".to_owned(),
                        delta_packets,
                    };
                }
            }
        }
    });

    let stream = PacketChannelBroadcastStream::new(packet_rx);
    let mut parser = AsyncStreamingParser::from_stream_with_visitor(stream, visitor)
        .context("failed to create streaming parser")?;

    let parser_result = tokio::select! {
        result = parser.run_to_end() => result,
        parent_result = parent_closed => {
            pump_task.abort();
            parent_result.context(
                "parent pipe monitor stopped unexpectedly",
            )?;
            anyhow::bail!("ThreatHudBridge parent pipe closed");
        }
    };

    if let Err(error) = parser_result {
        pump_task.abort();
        let _ = pump_task.await;
        return Err(error).context("dynamic Deadlock broadcast parser failed");
    }

    let visitor = parser.into_visitor();
    let reconnect = pump_task.await.context("broadcast packet task panicked")?;
    Ok((reconnect, visitor))
}

fn create_relay_http_client(
    relay_headers: &http::HeaderMap,
    traffic: Arc<TrafficCounters>,
) -> AnyResult<CountingHttpClient> {
    let client = reqwest::Client::builder()
        .default_headers(relay_headers.clone())
        .connect_timeout(RELAY_CONNECT_TIMEOUT)
        .build()
        .context("failed to create relay HTTP client")?;

    Ok(CountingHttpClient { client, traffic })
}

async fn next_required_bootstrap_packet(
    broadcast: &mut BroadcastHttp<'static, CountingHttpClient>,
    endpoint: &str,
) -> Result<Bytes, String> {
    match broadcast.next_packet().await {
        Some(Ok(packet)) if !packet.is_empty() => Ok(packet),
        Some(Ok(_)) => Err(format!("relay {endpoint} returned an empty body")),
        Some(Err(error)) => Err(format_relay_error(endpoint, error)),
        None => Err(format!("relay {endpoint} is not ready")),
    }
}

fn format_relay_error(endpoint: &str, error: BroadcastHttpClientError<reqwest::Error>) -> String {
    format!("relay {endpoint} failed: {:#}", anyhow::Error::new(error),)
}

fn monitor_parent_pipe() -> oneshot::Receiver<()> {
    let (closed_tx, closed_rx) = oneshot::channel();

    std::thread::spawn(move || {
        let mut input = io::stdin().lock();
        let mut buffer = [0_u8; 1];

        loop {
            match std::io::Read::read(&mut input, &mut buffer) {
                Ok(0) | Err(_) => break,
                Ok(_) => {}
            }
        }

        let _ = closed_tx.send(());
    });

    closed_rx
}

fn parse_arguments() -> AnyResult<Arguments> {
    let mut match_id = None;
    let mut broadcast_url = None;
    let mut args = std::env::args().skip(1);

    while let Some(argument) = args.next() {
        match argument.as_str() {
            "--match-id" => {
                let value = args.next().context("--match-id requires a value")?;
                match_id = Some(value.parse::<u64>().context("invalid --match-id")?);
            }
            "--broadcast-url" => {
                broadcast_url = Some(args.next().context("--broadcast-url requires a value")?);
            }
            "--help" | "-h" => {
                println!("ThreatHudBroadcastParser --match-id <id> --broadcast-url <url>");
                std::process::exit(0);
            }
            unknown => anyhow::bail!("unknown argument: {unknown}"),
        }
    }

    let match_id = match_id.context("missing --match-id")?;
    if match_id == 0 {
        anyhow::bail!("--match-id must not be zero");
    }

    let broadcast_url = broadcast_url.context("missing --broadcast-url")?;
    if !broadcast_url.starts_with("http://") && !broadcast_url.starts_with("https://") {
        anyhow::bail!("--broadcast-url must be an absolute HTTP or HTTPS URL");
    }

    Ok(Arguments {
        match_id,
        broadcast_url,
    })
}

fn emit_json_line<T: Serialize>(value: &T) -> Result<(), OutputError> {
    let stdout = io::stdout();
    let mut output = stdout.lock();
    serde_json::to_writer(&mut output, value)?;
    writeln!(&mut output)?;
    output.flush()?;
    Ok(())
}