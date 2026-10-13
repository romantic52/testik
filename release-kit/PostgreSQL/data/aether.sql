--
-- PostgreSQL database dump
--

\restrict RKolhOL0pTODSNeUjSKWdobjtByfCq5xVet3IFNc2yBBu4Ydrgh2DlL5add7kiL

-- Dumped from database version 16.15
-- Dumped by pg_dump version 16.15

SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

--
-- Name: account_no_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.account_no_seq
    START WITH 100000
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


SET default_tablespace = '';

SET default_table_access_method = heap;

--
-- Name: crypto_devices; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.crypto_devices (
    user_id text NOT NULL,
    device_id text NOT NULL,
    identity_key_b64 text NOT NULL,
    created_at text NOT NULL,
    ed25519_key_b64 text,
    identity_sig_b64 text,
    master_key_b64 text,
    device_sig_b64 text
);


--
-- Name: device_push_tokens; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.device_push_tokens (
    token text NOT NULL,
    user_id text NOT NULL,
    kind text DEFAULT 'apns'::text NOT NULL,
    updated_at text NOT NULL
);


--
-- Name: fallback_keys; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.fallback_keys (
    user_id text NOT NULL,
    device_id text NOT NULL,
    key_id text NOT NULL,
    key_b64 text NOT NULL,
    sig_b64 text,
    created_at text NOT NULL
);


--
-- Name: group_members; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.group_members (
    group_id text NOT NULL,
    user_id text NOT NULL,
    encrypted_key_b64 text NOT NULL,
    role text DEFAULT 'member'::text NOT NULL
);


--
-- Name: groups; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.groups (
    id text NOT NULL,
    name text NOT NULL,
    description text,
    owner_id text NOT NULL,
    is_channel integer DEFAULT 0 NOT NULL,
    linked_group_id text,
    created_at text NOT NULL,
    public_join integer DEFAULT 0 NOT NULL,
    join_key_b64 text,
    username text,
    avatar_file_id text
);


--
-- Name: history_backups; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.history_backups (
    seq bigint NOT NULL,
    user_id text NOT NULL,
    nonce_b64 text NOT NULL,
    ciphertext_b64 text NOT NULL,
    created_at text NOT NULL
);


--
-- Name: history_backups_seq_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.history_backups_seq_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: history_backups_seq_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.history_backups_seq_seq OWNED BY public.history_backups.seq;


--
-- Name: message_receipts; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.message_receipts (
    message_id text NOT NULL,
    user_id text NOT NULL,
    device_id text DEFAULT 'primary'::text NOT NULL,
    acked_at text NOT NULL
);


--
-- Name: messages; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.messages (
    id text NOT NULL,
    sender_id text NOT NULL,
    recipient_id text NOT NULL,
    envelope_json text NOT NULL,
    created_at text NOT NULL,
    delivered integer DEFAULT 0 NOT NULL,
    recipient_device_id text
);


--
-- Name: one_time_keys; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.one_time_keys (
    user_id text NOT NULL,
    device_id text DEFAULT 'primary'::text NOT NULL,
    key_id text NOT NULL,
    key_b64 text NOT NULL,
    sig_b64 text
);


--
-- Name: otk_claims; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.otk_claims (
    claimer_id text NOT NULL,
    user_id text NOT NULL,
    device_id text NOT NULL,
    claims integer DEFAULT 0 NOT NULL,
    window_start text NOT NULL
);


--
-- Name: pairing_requests; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.pairing_requests (
    pairing_id text NOT NULL,
    secret_hash text NOT NULL,
    eph_pub_b64 text NOT NULL,
    created_at text NOT NULL,
    user_id text,
    device_id text,
    encrypted_bundle_b64 text,
    platform text,
    approved_at text
);


--
-- Name: post_views; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.post_views (
    message_id text NOT NULL,
    user_id text NOT NULL,
    viewed_at text NOT NULL
);


--
-- Name: server_meta; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.server_meta (
    key text NOT NULL,
    value text NOT NULL
);


--
-- Name: sessions; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.sessions (
    token text NOT NULL,
    user_id text NOT NULL,
    created_at text NOT NULL,
    expires_at text,
    device_id text,
    device_bound_at text
);


--
-- Name: signed_devices; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.signed_devices (
    user_id text NOT NULL,
    device_id text NOT NULL,
    first_signed_at text NOT NULL,
    cross_signed integer DEFAULT 0 NOT NULL
);


--
-- Name: users; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.users (
    user_id text NOT NULL,
    public_key_b64 text NOT NULL,
    password_hash text,
    created_at text NOT NULL,
    status_emoji text,
    account_no bigint DEFAULT nextval('public.account_no_seq'::regclass),
    olm_identity_key text,
    encrypted_private_key_b64 text,
    encrypted_olm_account_b64 text,
    totp_secret text,
    totp_enabled integer DEFAULT 0 NOT NULL,
    username text,
    display_name text,
    avatar_data text,
    last_active text,
    avatar_file_id text,
    bio text
);


--
-- Name: history_backups seq; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.history_backups ALTER COLUMN seq SET DEFAULT nextval('public.history_backups_seq_seq'::regclass);


--
-- Name: crypto_devices crypto_devices_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.crypto_devices
    ADD CONSTRAINT crypto_devices_pkey PRIMARY KEY (user_id, device_id);


--
-- Name: device_push_tokens device_push_tokens_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.device_push_tokens
    ADD CONSTRAINT device_push_tokens_pkey PRIMARY KEY (token);


--
-- Name: fallback_keys fallback_keys_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.fallback_keys
    ADD CONSTRAINT fallback_keys_pkey PRIMARY KEY (user_id, device_id);


--
-- Name: group_members group_members_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.group_members
    ADD CONSTRAINT group_members_pkey PRIMARY KEY (group_id, user_id);


--
-- Name: groups groups_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.groups
    ADD CONSTRAINT groups_pkey PRIMARY KEY (id);


--
-- Name: history_backups history_backups_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.history_backups
    ADD CONSTRAINT history_backups_pkey PRIMARY KEY (seq);


--
-- Name: message_receipts message_receipts_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.message_receipts
    ADD CONSTRAINT message_receipts_pkey PRIMARY KEY (message_id, user_id, device_id);


--
-- Name: messages messages_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.messages
    ADD CONSTRAINT messages_pkey PRIMARY KEY (id);


--
-- Name: one_time_keys one_time_keys_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.one_time_keys
    ADD CONSTRAINT one_time_keys_pkey PRIMARY KEY (user_id, device_id, key_id);


--
-- Name: otk_claims otk_claims_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.otk_claims
    ADD CONSTRAINT otk_claims_pkey PRIMARY KEY (claimer_id, user_id, device_id);


--
-- Name: pairing_requests pairing_requests_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.pairing_requests
    ADD CONSTRAINT pairing_requests_pkey PRIMARY KEY (pairing_id);


--
-- Name: post_views post_views_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.post_views
    ADD CONSTRAINT post_views_pkey PRIMARY KEY (message_id, user_id);


--
-- Name: server_meta server_meta_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.server_meta
    ADD CONSTRAINT server_meta_pkey PRIMARY KEY (key);


--
-- Name: sessions sessions_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sessions
    ADD CONSTRAINT sessions_pkey PRIMARY KEY (token);


--
-- Name: signed_devices signed_devices_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.signed_devices
    ADD CONSTRAINT signed_devices_pkey PRIMARY KEY (user_id, device_id);


--
-- Name: users users_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.users
    ADD CONSTRAINT users_pkey PRIMARY KEY (user_id);


--
-- Name: idx_groups_username; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX idx_groups_username ON public.groups USING btree (lower(username));


--
-- Name: idx_history_backups_user; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_history_backups_user ON public.history_backups USING btree (user_id, seq);


--
-- Name: idx_users_username; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX idx_users_username ON public.users USING btree (username);


--
-- Name: users_account_no_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX users_account_no_idx ON public.users USING btree (account_no);


--
-- PostgreSQL database dump complete
--

\unrestrict RKolhOL0pTODSNeUjSKWdobjtByfCq5xVet3IFNc2yBBu4Ydrgh2DlL5add7kiL
