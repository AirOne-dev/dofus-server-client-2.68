package server

import (
	"context"
	"encoding/json"
	"log"
	"net/http"

	"divazaap/src/zaap"

	"github.com/apache/thrift/lib/go/thrift"
)

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
	transport, err := thrift.NewTServerSocket(zaapAddr)
	if err != nil {
		return nil, err
	}

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
