package server

import (
	"context"
	"encoding/json"
	"log"
	"net"
	"net/http"

	"divazaap/src/zaap"

	"github.com/apache/thrift/lib/go/thrift"
)

// Windows AIR's flash.net.Socket (cf. com.ankama.zaap.TFixedSocket.
// socketDataHandler) invokes the Thrift read callback exactly once per
// SOCKET_DATA event. If a single response spans several TCP segments, the
// parser hits a partial-read EOF on the first event and the remaining
// segments fire SOCKET_DATA into a nulled callback. Fix: each response must
// be one Write (TBufferedTransport in main.go) AND one segment (NoDelay
// below, otherwise Nagle would coalesce consecutive responses).
type nodelayListener struct{ net.Listener }

func (l nodelayListener) Accept() (net.Conn, error) {
	c, err := l.Listener.Accept()
	if err != nil {
		return c, err
	}
	if tcp, ok := c.(*net.TCPConn); ok {
		_ = tcp.SetNoDelay(true)
	}
	return c, nil
}

// thrift.TServerSocket creates its own net.Listener internally and gives us
// no hook to set per-conn socket options, so we implement TServerTransport
// directly over a pre-bound listener.
type nodelayServerTransport struct{ listener net.Listener }

func (t *nodelayServerTransport) Listen() error { return nil }
func (t *nodelayServerTransport) Accept() (thrift.TTransport, error) {
	conn, err := t.listener.Accept()
	if err != nil {
		return nil, thrift.NewTTransportExceptionFromError(err)
	}
	return thrift.NewTSocketFromConnConf(conn, &thrift.TConfiguration{}), nil
}
func (t *nodelayServerTransport) Close() error     { return t.listener.Close() }
func (t *nodelayServerTransport) Interrupt() error { return t.listener.Close() }

// RunningServer wraps the thrift server with the ability to stop it gracefully
type RunningServer struct {
	server     *thrift.TSimpleServer
	httpServer *http.Server
	Handler    *ZaapHandler
}

// Stop stops the running server gracefully
func (rs *RunningServer) Stop() {
	if rs.server != nil {
		log.Println("Stopping servers...")
		rs.server.Stop()
		if err := rs.httpServer.Shutdown(context.Background()); err != nil {
			log.Printf("Http server shutdown failed: %v", err)
		}
	}
}

// RunServer starts a new server instance and returns a RunningServer for controlling it
func RunServer(transportFactory thrift.TTransportFactory, protocolFactory thrift.TProtocolFactory, zaapAddr string, httpAddr string, authAddr string, ctx context.Context) (*RunningServer, error) {
	rawListener, err := net.Listen("tcp", zaapAddr)
	if err != nil {
		return nil, err
	}
	transport := &nodelayServerTransport{listener: nodelayListener{rawListener}}

	handler := NewZaapHandler()
	processor := zaap.NewZaapServiceProcessor(handler)
	server := thrift.NewTSimpleServer4(processor, transport, transportFactory, protocolFactory)
	httpServer := &http.Server{
		Addr: httpAddr,
		Handler: http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			if r.URL.Path == "/divazaap.json" {
				sample := map[string]interface{}{
					"gameAppId": 1,
					"connectionHosts": []string{
						"JMBouftou:" + authAddr,
					},
					"buildType":                  "release",
					"chatAppId":                  99,
					"chatServerHost":             "zaap-chat.ankama.com",
					"chatServerPort":             6337,
					"versionFileUrl":             "",
					"haapiAnkamaUrl":             "https://haapi.ankama.com/json/Ankama/v5/",
					"haapiDofusUrl":              "https://haapi.ankama.com/json/Dofus/v3/",
					"shopDofusUrl":               "https://shop-api.ankama.com/",
					"gamesActivityDescriptorUrl": "https://launcher.cdn.ankama.com/configs/useractivities.json",
					"avatarUrlFormat":            "https://avatar.ankama.com/users/{0}.png",
					"dofusWebsiteUrl":            "https://www.dofus.com",
				}
				w.Header().Set("Content-Type", "application/json")
				json.NewEncoder(w).Encode(sample)
			} else {
				http.NotFound(w, r)
			}
		}),
	}

	runningServer := &RunningServer{server: server, httpServer: httpServer, Handler: handler}

	// Run the zaap server in a separate goroutine
	go func() {
		log.Println("Starting the Zaap server on", zaapAddr)
		if err := server.Serve(); err != nil {
			log.Printf("Error running zaap server: %v", err)
		}
	}()

	// Run the http server in a separate goroutine
	go func() {
		log.Println("Starting the Http server on", httpAddr)
		if err := httpServer.ListenAndServe(); err != http.ErrServerClosed {
			log.Printf("Error running http server: %v\n", err)
		}
	}()

	// Watch for context cancellation to stop both servers
	go func() {
		<-ctx.Done()
		log.Println("Context canceled, stopping both servers...")
		server.Stop()
		if err := httpServer.Shutdown(context.Background()); err != nil {
			log.Printf("Http server shutdown failed: %v", err)
		}
	}()

	return runningServer, nil
}
