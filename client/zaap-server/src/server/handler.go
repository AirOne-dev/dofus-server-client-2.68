package server

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"sync"
)

type UserInfo struct {
	ID              string `json:"id"`
	Type            string `json:"type"`
	Login           string `json:"login"`
	Nickname        string `json:"nickname"`
	Firstname       string `json:"firstname"`
	Lastname        string `json:"lastname"`
	NicknameWithTag string `json:"nicknameWithTag"`
}

type ZaapClient struct {
	gameToken  string
	instanceId int32
	login      string
}

type ZaapHandler struct {
	clients map[string]*ZaapClient
	mu      sync.Mutex
}

func NewZaapHandler() *ZaapHandler {
	return &ZaapHandler{
		clients: make(map[string]*ZaapClient),
	}
}

func (p *ZaapHandler) Register(gameToken string, instanceId int32, hash string, login string) {
	log.Printf("Registering client gameToken=%s instanceId=%d hash=%s login=%s", gameToken, instanceId, hash, login)
	p.mu.Lock()
	defer p.mu.Unlock()
	p.clients[hash] = &ZaapClient{
		gameToken:  gameToken,
		instanceId: instanceId,
		login:      login,
	}
}

func (p *ZaapHandler) Connect(ctx context.Context, gameName string, releaseName string, instanceId int32, hash string) (string, error) {
	log.Printf("connect called with gameName: %s, releaseName: %s, instanceId: %d, hash: %s", gameName, releaseName, instanceId, hash)
	p.mu.Lock()
	defer p.mu.Unlock()
	if _, ok := p.clients[hash]; ok {
		return hash, nil
	}
	log.Printf("Client not found with hash: %s", hash)
	// TODO: Understand how to return errors in Zaap format?
	return "", nil
}

func (p *ZaapHandler) AuthGetGameToken(ctx context.Context, gameSession string, gameId int32) (string, error) {
	log.Printf("auth_get_game_token called with gameSession: %s, gameId: %d", gameSession, gameId)
	p.mu.Lock()
	defer p.mu.Unlock()
	if client, ok := p.clients[gameSession]; ok {
		return client.gameToken, nil
	}

	// TODO: Understand how to return errors in Zaap format?
	return "", nil
}

func (p *ZaapHandler) UpdaterIsUpdateAvailable(ctx context.Context, gameSession string) (bool, error) {
	log.Printf("updater_is_update_available called with gameSession: %s", gameSession)
	return false, nil
}

func (p *ZaapHandler) SettingsGet(ctx context.Context, gameSession string, key string) (string, error) {
	log.Printf("settings_get called with gameSession: %s, key: %s", gameSession, key)
	if key == "autoConnectType" {
		// 0 = server, 1 = character, 2 = game
		return "0", nil
	}
	return "", nil
}

func (p *ZaapHandler) SettingsSet(ctx context.Context, gameSession string, key string, value string) error {
	log.Printf("settings_set called with gameSession: %s, key: %s, value: %s", gameSession, key, value)
	return nil
}

func (p *ZaapHandler) UserInfoGet(ctx context.Context, gameSession string) (string, error) {
	log.Printf("user_info_get called with gameSession: %s", gameSession)
	p.mu.Lock()
	defer p.mu.Unlock()

	if client, ok := p.clients[gameSession]; ok {
		login := client.login
		if login == "" {
			login = fmt.Sprintf("Login%d", client.instanceId)
		}
		user := UserInfo{
			ID:              fmt.Sprintf("%d", client.instanceId),
			Type:            "ANKAMA",
			Login:           login,
			Nickname:        login,
			Firstname:       login,
			Lastname:        login,
			NicknameWithTag: login + "#0000",
		}

		// Convert struct to JSON
		jsonData, err := json.Marshal(user)
		if err != nil {
			fmt.Println("Error marshalling JSON:", err)
			return "", nil
		}

		// Convert JSON bytes to string
		jsonString := string(jsonData)
		return jsonString, nil
	}

	// TODO: Understand how to return errors in Zaap format?
	return "", nil

}

func (p *ZaapHandler) ReleaseRestartOnExit(ctx context.Context, gameSession string) error {
	log.Printf("release_restart_on_exit called with gameSession: %s", gameSession)
	return nil
}

func (p *ZaapHandler) ReleaseExitAndRepair(ctx context.Context, gameSession string) error {
	log.Printf("release_exit_and_repair called with gameSession: %s", gameSession)
	return nil
}

func (p *ZaapHandler) ZaapVersionGet(ctx context.Context, gameSession string) (string, error) {
	log.Printf("zaap_version_get called with gameSession: %s", gameSession)
	return "", nil
}

func (p *ZaapHandler) ZaapMustUpdateGet(ctx context.Context, gameSession string) (bool, error) {
	log.Printf("zaap_must_update_get called with gameSession: %s", gameSession)
	return false, nil
}

func (p *ZaapHandler) AuthGetGameTokenWithWindowId(ctx context.Context, gameSession string, gameId int32, windowId int32) (string, error) {
	log.Printf("auth_get_game_token_with_window_id called with gameSession: %s, gameId: %d, windowId: %d", gameSession, gameId, windowId)
	return "", nil
}
